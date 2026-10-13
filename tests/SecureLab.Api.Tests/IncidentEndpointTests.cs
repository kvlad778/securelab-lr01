using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureLab.Api.Data;
using SecureLab.Api.Presentation.Contracts;

namespace SecureLab.Api.Tests;

public sealed class IncidentEndpointTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetList_ReturnsSeededIncidents()
    {
        var incidents = await _client.GetFromJsonAsync<List<IncidentListItemResponse>>(
            "/api/incidents");

        Assert.NotNull(incidents);
        Assert.Contains(incidents, incident => incident.Id == DbSeeder.AliceIncidentId);
        Assert.Contains(incidents, incident => incident.Id == DbSeeder.BobIncidentId);
    }

    [Fact]
    public async Task GetDetails_ForUnknownId_ReturnsProblemDetails404()
    {
        using var response = await _client.GetAsync($"/api/incidents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetDetails_DoesNotExposeInternalOwnerFields()
    {
        using var response = await _client.GetAsync(
            $"/api/incidents/{DbSeeder.AliceIncidentId}");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(document.RootElement.TryGetProperty("ownerUserId", out _));
        Assert.False(document.RootElement.TryGetProperty("email", out _));
        Assert.Equal("Аліса Коваль", document.RootElement.GetProperty("ownerDisplayName").GetString());
    }

    [Fact]
    public async Task ClientScript_DoesNotUseDangerousInnerHtmlSink()
    {
        var script = await _client.GetStringAsync("/app.js");

        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.Contains("textContent", script, StringComparison.Ordinal);
    }


    [Fact]
    public async Task GetSeveritySummary_ReturnsGroupsSortedByCountThenName()
    {
        using var response = await _client.GetAsync("/api/incidents/severity-summary");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var items = document.RootElement.EnumerateArray().ToList();
        var severities = items
            .Select(item => item.GetProperty("severity").GetString())
            .ToArray();

        // ЛР 02: scaffold додав два інциденти Low, тому для штатного seed Low=3, High=1, Medium=1.
        Assert.Equal(new[] { "Low", "High", "Medium" }, severities);
        var counts = items
            .Select(item => item.GetProperty("count").GetInt32())
            .ToArray();
        Assert.Equal(new[] { 3, 1, 1 }, counts);
        foreach (var item in items)
        {
            Assert.Equal(2, item.EnumerateObject().Count());
        }
    }

    [Fact]
    public async Task GetSeveritySummary_WithStatus_FiltersGroups()
    {
        var summary = await _client.GetFromJsonAsync<List<IncidentSeveritySummaryResponse>>(
            "/api/incidents/severity-summary?status=Triaged");

        Assert.NotNull(summary);
        var only = Assert.Single(summary);
        Assert.Equal("Medium", only.Severity);
        Assert.Equal(1, only.Count);
    }

    [Fact]
    public async Task GetSeveritySummary_WithNoMatches_ReturnsEmptyArray()
    {
        var summary = await _client.GetFromJsonAsync<List<IncidentSeveritySummaryResponse>>(
            "/api/incidents/severity-summary?status=Resolved");

        Assert.NotNull(summary);
        Assert.Empty(summary);
    }

    [Fact]
    public async Task GetSeveritySummary_WithUnknownStatus_ReturnsValidationProblem400()
    {
        using var response = await _client.GetAsync("/api/incidents/severity-summary?status=Unknown");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
