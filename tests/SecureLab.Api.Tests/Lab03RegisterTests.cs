using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureLab.Api.Data;
using static SecureLab.Api.Tests.Lab03TestSupport;

namespace SecureLab.Api.Tests;

// Register: T-01, T-02 (автоматично), duplicate-контракт 409 і ігнорування зайвих полів.
public sealed class Lab03RegisterTests(SecureLabApiFactory factory) : IClassFixture<SecureLabApiFactory>
{
    private static object RegisterBody(string userName, string email, string displayName, string password) =>
        new { userName, email, displayName, password };

    // T-01: коректна реєстрація → 201, Location /api/me, лише безпечні поля.
    [Fact]
    public async Task T01_Register_ValidDto_Returns201WithSafeResponse()
    {
        var userName = "reg" + UniqueSuffix();
        using var client = NewHttpsClient(factory);

        using var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(userName, userName + "@example.test", "Студент 01", NewOneTimePassword()));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/me", response.Headers.Location?.OriginalString);
        Assert.Equal(userName, root.GetProperty("userName").GetString());
        Assert.Equal("Студент 01", root.GetProperty("displayName").GetString());
        Assert.NotEqual(Guid.Empty, root.GetProperty("id").GetGuid());
        foreach (var forbidden in new[] { "password", "confirmPassword", "passwordHash", "securityStamp", "email", "role", "cookie", "token" })
        {
            Assert.False(root.TryGetProperty(forbidden, out _), $"Відповідь містить заборонене поле {forbidden}.");
        }
        // Реєстрація не виконує автоматичний login.
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // T-02 + duplicate-контракт: повтор імені або email → однаковий 409 Problem Details.
    [Fact]
    public async Task Duplicate_UserNameOrEmail_Returns409WithSameProblem()
    {
        var userName = "dup" + UniqueSuffix();
        var email = userName + "@example.test";
        using var client = NewHttpsClient(factory);
        using var first = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(userName, email, "Дублікат", NewOneTimePassword()));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var sameUserName = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(userName, "other" + UniqueSuffix() + "@example.test", "Дублікат", NewOneTimePassword()));
        using var sameEmail = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody("other" + UniqueSuffix(), email, "Дублікат", NewOneTimePassword()));

        Assert.Equal(HttpStatusCode.Conflict, sameUserName.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);
        Assert.Equal("application/problem+json", sameUserName.Content.Headers.ContentType?.MediaType);
        var byName = await ProblemShape.ReadAsync(sameUserName);
        var byEmail = await ProblemShape.ReadAsync(sameEmail);
        // Відповідь не підказує, що саме зайняте — ім'я чи email.
        Assert.Equal(byName.Title, byEmail.Title);
        Assert.Equal(byName.Detail, byEmail.Detail);
    }

    // T-02: пароль не за policy → 400 з ключем password; пароль у відповідь не повертається.
    [Fact]
    public async Task T02_Register_WeakPassword_Returns400WithPasswordKey()
    {
        var userName = "weak" + UniqueSuffix();
        using var client = NewHttpsClient(factory);

        using var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(userName, userName + "@example.test", "Слабкий пароль", "short"));
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("password", out _));
        Assert.DoesNotContain("short", body, StringComparison.Ordinal);
    }

    // T-02: displayName зі 121 символу (межа моделі 120) → 400 з ключем displayName, а не 500.
    [Fact]
    public async Task T02_Register_DisplayNameTooLong_Returns400()
    {
        var userName = "long" + UniqueSuffix();
        using var client = NewHttpsClient(factory);

        using var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(userName, userName + "@example.test", new string('Я', 121), NewOneTimePassword()));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("displayName", out _));
    }

    // Зайві поля role і id ігноруються: користувач отримує роль Reporter і серверний Id.
    [Fact]
    public async Task Register_ExtraRoleAndIdFields_AreIgnored()
    {
        var userName = "extra" + UniqueSuffix();
        var password = NewOneTimePassword();
        var forgedId = Guid.NewGuid();
        using var client = NewHttpsClient(factory);

        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            userName,
            email = userName + "@example.test",
            displayName = "Зайві поля",
            password,
            role = DbSeeder.AdministratorRole,
            id = forgedId
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(forgedId, await ReadCreatedIdAsync(response));

        using var login = await PostLoginAsync(client, userName, password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var me = await client.GetAsync("/api/me");
        using var json = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(DbSeeder.ReporterRole, json.RootElement.GetProperty("role").GetString());
    }
}
