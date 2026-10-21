using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureLab.Api.Data;
using static SecureLab.Api.Tests.Lab03TestSupport;

namespace SecureLab.Api.Tests;

// Власні безпекові регресійні сценарії ЛР 03: A-01 … A-07.
public sealed class Lab03AuthTests(SecureLabApiFactory factory) : IClassFixture<SecureLabApiFactory>
{
    // A-01 (достатній): анонімний запит до захищеного POST /api/incidents → 401, без redirect.
    [Fact]
    public async Task A01_AnonymousCreateIncident_Returns401ProblemWithoutRedirect()
    {
        var title = "A01 " + UniqueSuffix();
        using var anonymous = NewHttpsClient(factory);

        using var response = await anonymous.PostAsJsonAsync("/api/incidents", IncidentBody(title));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        // Код endpoint не виконався: запис не створено.
        Assert.Equal(0, await CountIncidentsByTitleAsync(factory, title));
    }

    // A-02 (достатній): login → /api/me повертає користувача з cookie/principal і лише безпечні поля.
    [Fact]
    public async Task A02_LoginThenMe_ReturnsVerifiedAliceWithSafeFields()
    {
        using var alice = await factory.LoginAsync("alice", StudyPassword);

        using var response = await alice.GetAsync("/api/me");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DbSeeder.AliceId, root.GetProperty("userId").GetGuid());
        Assert.Equal("alice", root.GetProperty("userName").GetString());
        Assert.Equal(DbSeeder.ReporterRole, root.GetProperty("role").GetString());
        foreach (var forbidden in new[] { "password", "passwordHash", "securityStamp", "concurrencyStamp", "cookie", "token" })
        {
            Assert.False(root.TryGetProperty(forbidden, out _), $"Відповідь містить заборонене поле {forbidden}.");
        }
    }

    // A-03 (добрий): чинна cookie Alice + X-Demo-UserId Bob → /api/me лишається Alice.
    [Fact]
    public async Task A03_MeWithForgedDemoHeader_StaysCurrentPrincipal()
    {
        using var alice = await factory.LoginAsync("alice", StudyPassword);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("X-Demo-UserId", DbSeeder.BobId.ToString());

        using var response = await alice.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DbSeeder.AliceId, json.RootElement.GetProperty("userId").GetGuid());
    }

    // A-04 (добрий): Bob створює запис без owner у DTO → owner нового запису = Bob (з principal).
    [Fact]
    public async Task A04_BobCreatesIncidentWithoutOwner_OwnerIsBob()
    {
        var title = "A04 " + UniqueSuffix();
        try
        {
            using var bob = await factory.LoginAsync("bob", StudyPassword);

            using var response = await bob.PostAsJsonAsync("/api/incidents", IncidentBody(title));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = await ReadCreatedIdAsync(response);
            Assert.Equal(DbSeeder.BobId, await ReadOwnerAsync(factory, id));
        }
        finally
        {
            await DeleteIncidentsByTitleAsync(factory, title);
        }
    }

    // A-05 (достатній): Bob надсилає чужий ownerUserId Alice → поле ігнорується, owner = Bob.
    [Fact]
    public async Task A05_BobCreatesIncidentWithForgedAliceOwner_OwnerStaysBob()
    {
        var title = "A05 " + UniqueSuffix();
        try
        {
            using var bob = await factory.LoginAsync("bob", StudyPassword);

            using var response = await bob.PostAsJsonAsync(
                "/api/incidents", IncidentBody(title, forgedOwnerUserId: DbSeeder.AliceId));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.False(json.RootElement.TryGetProperty("ownerUserId", out _));
            var id = json.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(DbSeeder.BobId, await ReadOwnerAsync(factory, id));
        }
        finally
        {
            await DeleteIncidentsByTitleAsync(factory, title);
        }
    }

    // A-06 (добрий): login → захищений запит 200 → logout → той самий клієнт отримує 401.
    [Fact]
    public async Task A06_Logout_ThenSameClientGets401()
    {
        using var alice = await factory.LoginAsync("alice", StudyPassword);
        using var before = await alice.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var logout = await alice.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        // Основну cookie сервер наказує видалити: дата в минулому (значення не читаємо й не друкуємо).
        // AuthCookieAttributes повертає лише атрибути, тож повідомлення про помилку не містить значення cookie.
        var mainCookie = Assert.Single(AuthCookieAttributes(logout));
        Assert.Contains(mainCookie, part => part.StartsWith("expires=thu, 01 jan 1970", StringComparison.Ordinal));

        using var after = await alice.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // A-07 (добрий): невідомий користувач і хибний пароль → однаковий зовнішній результат.
    [Fact]
    public async Task A07_UnknownUserAndWrongPassword_HaveSameExternalResult()
    {
        using var client = NewHttpsClient(factory);

        using var unknownUser = await PostLoginAsync(client, "nouser" + UniqueSuffix(), StudyPassword);
        using var wrongPassword = await PostLoginAsync(client, "alice", NewOneTimePassword());

        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(unknownUser.StatusCode, wrongPassword.StatusCode);
        Assert.Equal(
            unknownUser.Content.Headers.ContentType?.MediaType,
            wrongPassword.Content.Headers.ContentType?.MediaType);
        Assert.False(unknownUser.Headers.Contains("Set-Cookie"));
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));

        var first = await ProblemShape.ReadAsync(unknownUser);
        var second = await ProblemShape.ReadAsync(wrongPassword);
        Assert.Equal(first.HasType, second.HasType);
        Assert.Equal(first.HasTitle, second.HasTitle);
        Assert.Equal(first.HasStatus, second.HasStatus);
        Assert.Equal(first.HasDetail, second.HasDetail);
        Assert.Equal(first.Type, second.Type);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Detail, second.Detail);
        // traceId різний у різних запитах — його рівність навмисно не вимагаємо.
    }
}
