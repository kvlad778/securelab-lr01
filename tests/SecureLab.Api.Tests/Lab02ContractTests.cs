using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureLab.Api.Data;

namespace SecureLab.Api.Tests;

// ЛР 02: автоматичні перевірки контракту POST /api/incidents (T-01, T-02, T-03, T-09).
// Кожен тест, що створює дані, прибирає лише власні записи у finally.
public sealed class Lab02ContractTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // T-01 + CP-01: коректне створення; зайві server-managed поля JSON ігноруються.
    [Fact]
    public async Task Post_ValidRequestWithExtraFields_Returns201AndServerManagedValues()
    {
        var title = $"T01-{Guid.NewGuid():N}";
        var sentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var body = new
        {
            title = "  " + title + "  ",
            description = "Штучний запис для перевірки коректного створення.",
            severity = "medium",
            occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            id = sentId,
            ownerUserId = DbSeeder.BobId,
            status = "Closed",
            createdAtUtc = "2000-01-01T00:00:00Z"
        };

        try
        {
            using var response = await _client.PostAsJsonAsync("/api/incidents", body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var location = response.Headers.Location;
            Assert.NotNull(location);

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(title, root.GetProperty("title").GetString());
            Assert.Equal("Medium", root.GetProperty("severity").GetString());
            Assert.Equal("New", root.GetProperty("status").GetString());
            Assert.NotEqual(sentId, root.GetProperty("id").GetGuid());
            Assert.False(root.TryGetProperty("ownerUserId", out _));

            using var details = await _client.GetAsync(location);
            Assert.Equal(HttpStatusCode.OK, details.StatusCode);
            using var detailsDocument = JsonDocument.Parse(await details.Content.ReadAsStringAsync());
            Assert.Equal("Аліса Коваль",
                detailsDocument.RootElement.GetProperty("ownerDisplayName").GetString());
            Assert.Equal("New", detailsDocument.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await DeleteByTitleAsync(title);
        }
    }

    // T-02: некоректний DTO — порожній title, severity "7", відсутній occurredAtUtc.
    [Fact]
    public async Task Post_InvalidDto_Returns400ProblemDetailsWithFieldKeys()
    {
        var body = new
        {
            title = "   ",
            description = "Опис є, але інші поля некоректні.",
            severity = "7"
        };

        using var response = await _client.PostAsJsonAsync("/api/incidents", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var errors = document.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("title", out _));
        Assert.True(errors.TryGetProperty("severity", out _));
        Assert.True(errors.TryGetProperty("occurredAtUtc", out _));
        Assert.False(errors.TryGetProperty("description", out _));
        AssertNoInternalDetails(json);
    }

    // T-02 (додатково): дата більш ніж на 5 хвилин у майбутньому і title довший за 160.
    [Fact]
    public async Task Post_FutureDateAndTooLongTitle_Returns400()
    {
        var body = new
        {
            title = new string('t', 161),
            description = "Опис для перевірки меж довжини та дати.",
            severity = "Low",
            occurredAtUtc = "2099-01-01T00:00:00Z"
        };

        using var response = await _client.PostAsJsonAsync("/api/incidents", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var errors = document.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("title", out _));
        Assert.True(errors.TryGetProperty("occurredAtUtc", out _));
        AssertNoInternalDetails(json);
    }

    // T-03: другий незакритий інцидент із тим самим title -> 409 Problem Details.
    [Fact]
    public async Task Post_DuplicateActiveTitle_Returns409()
    {
        var title = $"Regression-{Guid.NewGuid():N}";
        var body = new
        {
            title,
            description = "Штучний запис для перевірки предметного конфлікту.",
            severity = "Low",
            occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        };

        try
        {
            using var first = await _client.PostAsJsonAsync("/api/incidents", body);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

            using var second = await _client.PostAsJsonAsync("/api/incidents", body);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);

            var json = await second.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            Assert.Equal(409, document.RootElement.GetProperty("status").GetInt32());
            Assert.True(document.RootElement.TryGetProperty("detail", out _));
            Assert.False(document.RootElement.TryGetProperty("errors", out _));
            AssertNoInternalDetails(json);
        }
        finally
        {
            await DeleteByTitleAsync(title);
        }
    }

    // T-09: межа cross-field правила для High — 39 символів відхиляється, 40 проходить.
    [Fact]
    public async Task Post_HighSeverityDescriptionBoundary_39Rejected_40Accepted()
    {
        var title39 = $"T09-39-{Guid.NewGuid():N}";
        var title40 = $"T09-40-{Guid.NewGuid():N}";

        try
        {
            using var rejected = await _client.PostAsJsonAsync("/api/incidents", new
            {
                title = title39,
                description = "  " + new string('x', 39) + "  ",
                severity = "High",
                occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var json = await rejected.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("description", out _));

            using var accepted = await _client.PostAsJsonAsync("/api/incidents", new
            {
                title = title40,
                description = new string('x', 40),
                severity = "High",
                occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            });
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
        finally
        {
            await DeleteByTitleAsync(title39);
            await DeleteByTitleAsync(title40);
        }
    }

    private static void AssertNoInternalDetails(string json)
    {
        Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DeleteByTitleAsync(string title)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        await db.Incidents.Where(item => item.Title == title).ExecuteDeleteAsync();
    }
}
