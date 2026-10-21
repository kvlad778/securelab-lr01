using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SecureLab.Api.Tests;

public sealed class SecureLabApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // ЛР 03: у тестовому сервері всі клієнти мають RemoteIpAddress = null і потрапляють
        // в одну групу ліміту login. Для звичайних тестів ліміт піднято; сам ліміт перевіряє
        // окремий тест Lab03CookieAndRateLimitTests на свіжому host із малим вікном.
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LoginRateLimit:PermitLimit"] = "1000"
            }));
    }
}
