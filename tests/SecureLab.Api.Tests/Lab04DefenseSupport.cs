using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureLab.Api.Data;
using Xunit.Abstractions;
using static SecureLab.Api.Tests.Lab03TestSupport;
using static SecureLab.Api.Tests.Lab04DefenseSupport;

namespace SecureLab.Api.Tests;

internal static class Lab04DefenseSupport
{
    public const string FixtureTitle = "DEF-B: одноразовий інцидент Bob";
    public const string FixtureDescription = "Disposable fixture B для захисту ЛР 04, власник Bob.";

    public static readonly DateTimeOffset FixtureOccurredAtUtc = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    public static string Now() => DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static string Who(Guid userId) =>
        userId == DbSeeder.AliceId ? "Alice" : userId == DbSeeder.BobId ? "Bob" : userId.ToString();

    public static async Task<MeView> MeAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new MeView(
            json.RootElement.GetProperty("userId").GetGuid(),
            json.RootElement.GetProperty("userName").GetString() ?? string.Empty,
            json.RootElement.GetProperty("role").GetString() ?? string.Empty);
    }

    public static async Task<FixtureState?> TryFindFixtureBAsync(SecureLabApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        return await db.Incidents.AsNoTracking()
            .Where(item => item.OwnerUserId == DbSeeder.BobId && item.OccurredAtUtc == FixtureOccurredAtUtc)
            .Select(item => new FixtureState(
                item.Id, item.Title, item.Description, item.OwnerUserId, item.Status.ToString(), item.UpdatedAtUtc))
            .SingleOrDefaultAsync();
    }

    public static async Task<FixtureState> FindFixtureBAsync(SecureLabApiFactory factory) =>
        await TryFindFixtureBAsync(factory)
        ?? throw new InvalidOperationException(
            "Fixture B не знайдено: спершу запусти Lab04DefenseFixtureTests.PrepareOrRestoreFixtureB.");

    public static async Task<string> ReadAsOwnerAsync(HttpClient ownerClient, Guid id)
    {
        using var response = await ownerClient.GetAsync($"/api/incidents/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        return $"title=\"{root.GetProperty("title").GetString()}\"; owner={root.GetProperty("ownerDisplayName").GetString()}; "
            + $"status={root.GetProperty("status").GetString()}";
    }

    public static string Describe(FixtureState state) =>
        $"title=\"{state.Title}\"; owner={Who(state.OwnerUserId)}; status={state.Status}; "
        + $"updatedAt={state.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}";
}

internal sealed record MeView(Guid UserId, string UserName, string Role);

internal sealed record FixtureState(
    Guid Id, string Title, string Description, Guid OwnerUserId, string Status, DateTimeOffset UpdatedAtUtc);

public sealed class Lab04DefenseFixtureTests(SecureLabApiFactory factory, ITestOutputHelper output)
    : IClassFixture<SecureLabApiFactory>
{
    [Fact]
    public async Task PrepareOrRestoreFixtureB()
    {
        using var bob = NewHttpsClient(factory);
        using var login = await PostLoginAsync(bob, "bob", StudyPassword);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var existing = await TryFindFixtureBAsync(factory);
        if (existing is null)
        {
            using var create = await bob.PostAsJsonAsync("/api/incidents", new
            {
                title = FixtureTitle,
                description = FixtureDescription,
                severity = "Low",
                occurredAtUtc = FixtureOccurredAtUtc
            });
            output.WriteLine($"{Now()} | B ще не було → POST від Bob → {(int)create.StatusCode}");
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }
        else
        {
            output.WriteLine($"{Now()} | ДОКАЗ до відновлення: {Describe(existing)}");
            using var restore = await bob.PutAsJsonAsync(
                $"/api/incidents/{existing.Id}", new { title = FixtureTitle, description = FixtureDescription });
            output.WriteLine($"{Now()} | відновлюю лише B: PUT від власника Bob → {(int)restore.StatusCode}");
            Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        }

        var b = await FindFixtureBAsync(factory);
        output.WriteLine($"{Now()} | B id = {b.Id} (I2 = {DbSeeder.BobIncidentId})");
        output.WriteLine($"{Now()} | B очима Bob: {await ReadAsOwnerAsync(bob, b.Id)}");
        output.WriteLine($"{Now()} | B у сховищі: {Describe(b)}");

        Assert.NotEqual(DbSeeder.BobIncidentId, b.Id);
        Assert.Equal(FixtureTitle, b.Title);
        Assert.Equal(DbSeeder.BobId, b.OwnerUserId);
    }

    [Fact]
    public async Task ObserveSharedJar_MeAndFixtureB()
    {
        var b = await FindFixtureBAsync(factory);
        using var client = NewHttpsClient(factory);

        using var loginAlice = await PostLoginAsync(client, "alice", StudyPassword);
        var afterAlice = await MeAsync(client);
        output.WriteLine($"{Now()} | login alice → {(int)loginAlice.StatusCode}; /api/me → {afterAlice.UserName} ({afterAlice.UserId}) {afterAlice.Role}");

        using var loginBob = await PostLoginAsync(client, "bob", StudyPassword);
        var afterBob = await MeAsync(client);
        output.WriteLine($"{Now()} | login bob у ТОЙ САМИЙ jar → {(int)loginBob.StatusCode}; /api/me → {afterBob.UserName} ({afterBob.UserId}) {afterBob.Role}");

        using var bob = NewHttpsClient(factory);
        using var bobLogin = await PostLoginAsync(bob, "bob", StudyPassword);
        Assert.Equal(HttpStatusCode.NoContent, bobLogin.StatusCode);
        output.WriteLine($"{Now()} | стан B очима Bob: {await ReadAsOwnerAsync(bob, b.Id)}");
        output.WriteLine($"{Now()} | стан B у сховищі: {Describe(b)}");

        Assert.Equal(HttpStatusCode.NoContent, loginAlice.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, loginBob.StatusCode);
        Assert.Equal(DbSeeder.AliceId, afterAlice.UserId);
        Assert.Equal(DbSeeder.BobId, afterBob.UserId);
    }
}
