using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using static SecureLab.Api.Tests.Lab03TestSupport;

namespace SecureLab.Api.Tests;

// Добрий рівень: фактичні атрибути cookie та обмеження частоти login.
public sealed class Lab03CookieAndRateLimitTests(SecureLabApiFactory factory) : IClassFixture<SecureLabApiFactory>
{
    private const int TestPermitLimit = 3;
    private static readonly TimeSpan TestWindow = TimeSpan.FromSeconds(5);

    // Cookie flags у HTTPS-сценарії: httponly, secure, samesite=lax, path=/; cookie сеансова (без expires).
    [Fact]
    public async Task CookieFlags_LoginOverHttps_HasHttpOnlySecureLaxSessionCookie()
    {
        using var client = NewHttpsClient(factory);

        using var response = await PostLoginAsync(client, "alice", StudyPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var attributes = Assert.Single(AuthCookieAttributes(response));
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, part => part.StartsWith("expires=", StringComparison.Ordinal));
        Assert.DoesNotContain(attributes, part => part.StartsWith("max-age=", StringComparison.Ordinal));
    }

    // Межа локального HTTP: у Development політика SameAsRequest, тож через HTTP secure немає.
    // Це пояснює, чому локальний HTTP trace не доводить Secure.
    [Fact]
    public async Task CookieFlags_LoginOverHttpInDevelopment_HasNoSecure()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });

        using var response = await PostLoginAsync(client, "alice", StudyPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var attributes = Assert.Single(AuthCookieAttributes(response));
        Assert.Contains("httponly", attributes);
        Assert.DoesNotContain("secure", attributes);
    }

    // Rate limit login: після ліміту → 429 (і для справжнього пароля), потім відновлення після вікна.
    [Fact]
    public async Task RateLimit_LoginAttemptsOverLimit_Return429ThenRecoverAfterWindow()
    {
        // Окремий host зі своїм лічильником і малим вікном; основний factory не зачіпається.
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LoginRateLimit:PermitLimit"] = TestPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["LoginRateLimit:WindowSeconds"] = TestWindow.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })));
        using var client = NewHttpsClient(host);

        for (var i = 0; i < TestPermitLimit; i++)
        {
            using var allowed = await PostLoginAsync(client, "nouser" + UniqueSuffix(), NewOneTimePassword());
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        // Понад ліміт: 429 і для невідомого імені, і для правильних даних — існування акаунта не розкривається.
        using var limitedUnknown = await PostLoginAsync(client, "nouser" + UniqueSuffix(), NewOneTimePassword());
        using var limitedValid = await PostLoginAsync(client, "alice", StudyPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedUnknown.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedValid.StatusCode);
        Assert.Equal("application/problem+json", limitedValid.Content.Headers.ContentType?.MediaType);
        Assert.False(limitedValid.Headers.Contains("Set-Cookie"));

        // Після завершення вікна (плюс невеликий запас) вхід знову можливий.
        await Task.Delay(TestWindow + TimeSpan.FromMilliseconds(500));
        using var recovered = await PostLoginAsync(client, "alice", StudyPassword);
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
    }
}
