using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;
using static SecureLab.Api.Tests.Lab03TestSupport;
using static SecureLab.Api.Tests.Lab04DefenseSupport;

namespace SecureLab.Api.Tests;

// Захист ЛР 04, «Дано»: дефектний тест update чужої fixture B.
public sealed class Lab04DefenseSubjectTests(SecureLabApiFactory factory, ITestOutputHelper output)
    : IClassFixture<SecureLabApiFactory>
{
    [Fact]
    public async Task AliceUpdatesBobFixtureB_IsDenied()
    {
        // B: disposable fixture власника Bob, створена до відліку (не I2, не seed).
        var b = await FindFixtureBAsync(factory);

        // Один client = один cookie jar.
        using var client = NewHttpsClient(factory);

        using var loginAlice = await PostLoginAsync(client, "alice", StudyPassword);
        Assert.Equal(HttpStatusCode.NoContent, loginAlice.StatusCode);

        using var loginBob = await PostLoginAsync(client, "bob", StudyPassword);
        Assert.Equal(HttpStatusCode.NoContent, loginBob.StatusCode);

        var alice = client;

        using var update = await alice.PutAsJsonAsync($"/api/incidents/{b.Id}", new
        {
            title = "DEF-B: змінено через змінну alice",
            description = "Спроба Alice змінити fixture B власника Bob."
        });
        output.WriteLine($"{Now()} | PUT B через змінну alice → {(int)update.StatusCode}");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }
}
