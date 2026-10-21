using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureLab.Api.Data;

namespace SecureLab.Api.Tests;

// Спільні допоміжні методи для власних тестів ЛР 03. Значення паролів і cookie не друкуються.
public static class Lab03TestSupport
{
    public const string AuthCookieName = ".AspNetCore.Identity.Application";

    // Навчальний пароль seed-акаунтів береться лише зі змінної середовища (поза Git).
    public static string StudyPassword =>
        Environment.GetEnvironmentVariable("SeedUsers__Password")
        ?? throw new InvalidOperationException("Set local SeedUsers__Password outside Git.");

    // Новий одноразовий пароль, що відповідає policy (10+ символів, A-Z, a-z, цифра, символ).
    public static string NewOneTimePassword() =>
        "Tst-" + Guid.NewGuid().ToString("N")[..10] + "Aa1!";

    public static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..8];

    // Новий HTTPS-клієнт без cookie, який не ходить за redirect і зберігає cookie.
    public static HttpClient NewHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    // Тіло POST /api/incidents за контрактом ЛР 2. ownerUserId додається лише для A-05.
    public static object IncidentBody(string title, Guid? forgedOwnerUserId = null) =>
        forgedOwnerUserId is null
            ? new
            {
                title,
                description = "Навчальний інцидент для перевірки власника ЛР 03.",
                severity = "Low",
                occurredAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
            }
            : new
            {
                title,
                description = "Навчальний інцидент для перевірки власника ЛР 03.",
                severity = "Low",
                occurredAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                ownerUserId = forgedOwnerUserId
            };

    public static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    // Прийом 4: фактичний OwnerUserId із БД (API це поле назовні не віддає).
    public static async Task<Guid> ReadOwnerAsync(WebApplicationFactory<Program> factory, Guid incidentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        var incident = await db.Incidents.AsNoTracking().SingleAsync(item => item.Id == incidentId);
        return incident.OwnerUserId;
    }

    // Видаляємо лише власні тестові записи, щоб не змінювати лічильники тестів ЛР 1.
    public static async Task DeleteIncidentsByTitleAsync(WebApplicationFactory<Program> factory, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        await db.Incidents.Where(item => item.Title == title).ExecuteDeleteAsync();
    }

    public static async Task<int> CountIncidentsByTitleAsync(WebApplicationFactory<Program> factory, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        return await db.Incidents.CountAsync(item => item.Title == title);
    }

    // Атрибути основної auth cookie без її значення (перша частина «назва=значення» відкидається).
    public static string[][] AuthCookieAttributes(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return [];
        }

        return values
            .Where(value => value.StartsWith(AuthCookieName + "=", StringComparison.Ordinal))
            .Select(value => value.Split(';').Skip(1)
                .Select(part => part.Trim().ToLowerInvariant())
                .ToArray())
            .ToArray();
    }

    public static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string userName, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { userName, password });
}
