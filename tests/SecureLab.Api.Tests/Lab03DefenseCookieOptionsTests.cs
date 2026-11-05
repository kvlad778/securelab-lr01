using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using static SecureLab.Api.Tests.Lab03TestSupport;

namespace SecureLab.Api.Tests;

// Захист ЛР 03: named options cookie і фактичний атрибут secure.

public sealed class Lab03DefenseCookieOptionsTests(SecureLabApiFactory factory, ITestOutputHelper output)
    : IClassFixture<SecureLabApiFactory>
{
   
    private static readonly string OptionsName = IdentityConstants.ApplicationScheme;

    // Реальна схема, якою Identity видає основну cookie: "Identity.Application".
    private static readonly string RealScheme = IdentityConstants.ApplicationScheme;

    [Fact]
    public async Task LoginBob_OverHttp_SecureDependsOnNamedOptions()
    {
        await using var app = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.PostConfigure<CookieAuthenticationOptions>(OptionsName, o =>
                    o.Cookie.SecurePolicy = CookieSecurePolicy.Always)));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false, HandleCookies = false
        });

        using var response = await PostLoginAsync(client, "bob", StudyPassword);

        // Зіставлення: authentication scheme ↔ cookie name ↔ named options.
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var monitor = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var defaultScheme = await schemes.GetDefaultAuthenticateSchemeAsync();
        var realOptions = monitor.Get(RealScheme);

        // Безпечний розбір фактичного Set-Cookie: лише атрибути основної cookie, значення відкинуто.
        var attributes = Assert.Single(AuthCookieAttributes(response));
        var hasSecure = attributes.Contains("secure");

        output.WriteLine($"PostConfigure name  = {OptionsName}");
        output.WriteLine($"Default auth scheme = {defaultScheme?.Name}; cookie name = {realOptions.Cookie.Name}; SecurePolicy = {realOptions.Cookie.SecurePolicy}");
        output.WriteLine($"login = {(int)response.StatusCode}; attributes = {string.Join("; ", attributes)}");
        output.WriteLine($"secure = {(hasSecure ? "Є" : "НЕМАЄ")}");

        Assert.Equal(RealScheme, defaultScheme?.Name);
        Assert.Equal(AuthCookieName, realOptions.Cookie.Name);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // Прогноз: secure з'являється лише тоді, коли Always потрапляє в options реальної схеми.
        Assert.Equal(OptionsName == RealScheme, hasSecure);
    }
}
