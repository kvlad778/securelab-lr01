using System.Net;
using System.Net.Http.Json;
using SecureLab.Api.Data;
using Xunit.Abstractions;
using static SecureLab.Api.Tests.Lab03TestSupport;
using static SecureLab.Api.Tests.Lab04DefenseSupport;

namespace SecureLab.Api.Tests;

// Захист ЛР 04: виправлений тест. Subject доводиться через /api/me, а не через ім'я змінної.
public sealed class Lab04DefenseSubjectTests(SecureLabApiFactory factory, ITestOutputHelper output)
    : IClassFixture<SecureLabApiFactory>
{
    private const string BobNewTitle = "DEF-B: змінено власником Bob";

    [Fact]
    public async Task AliceUpdatesBobFixtureB_IsDenied_BobUpdate_IsAllowed()
    {
        // B: disposable fixture власника Bob, створена до відліку (не I2, не seed).
        var b = await FindFixtureBAsync(factory);

        // ВИПРАВЛЕННЯ 1: окремий client (окремий cookie jar) для кожного subject.
        using var alice = NewHttpsClient(factory);
        using var bob = NewHttpsClient(factory);

        using var loginAlice = await PostLoginAsync(alice, "alice", StudyPassword);
        Assert.Equal(HttpStatusCode.NoContent, loginAlice.StatusCode);
        using var loginBob = await PostLoginAsync(bob, "bob", StudyPassword);
        Assert.Equal(HttpStatusCode.NoContent, loginBob.StatusCode);

        var before = await FindFixtureBAsync(factory);
        output.WriteLine($"{Now()} | стан B до     | {Describe(before)}");
        Assert.Equal(DbSeeder.BobId, before.OwnerUserId);

        // ВИПРАВЛЕННЯ 2: subject-перевірка безпосередньо перед запитом (/api/me, а не ім'я змінної).
        var subjectA = await MeAsync(alice);
        Assert.Equal(DbSeeder.AliceId, subjectA.UserId);

        // Прогноз: Alice → B = deny (404 за політикою ЛР 4), B без змін.
        using var deny = await alice.PutAsJsonAsync($"/api/incidents/{b.Id}", new
        {
            title = "DEF-B: спроба Alice",
            description = "Спроба Alice змінити fixture B власника Bob."
        });
        var afterDeny = await FindFixtureBAsync(factory);
        output.WriteLine($"{Now()} | subject={subjectA.UserName} ({subjectA.UserId}) | PUT B → {(int)deny.StatusCode} | {Describe(afterDeny)}");
        Assert.Equal(HttpStatusCode.NotFound, deny.StatusCode);

        // Незалежна звірка незмінності: сховище (усі поля, включно з updatedAt) і читання власником Bob.
        Assert.Equal(before, afterDeny);
        output.WriteLine($"{Now()} | B очима Bob  | {await ReadAsOwnerAsync(bob, b.Id)}");

        var subjectB = await MeAsync(bob);
        Assert.Equal(DbSeeder.BobId, subjectB.UserId);

        // Прогноз: Bob → B = allow (204), змінюється лише content; owner і status ті самі.
        using var allow = await bob.PutAsJsonAsync($"/api/incidents/{b.Id}", new
        {
            title = BobNewTitle,
            description = "Власник Bob оновив свою fixture B."
        });
        var afterAllow = await FindFixtureBAsync(factory);
        output.WriteLine($"{Now()} | subject={subjectB.UserName} ({subjectB.UserId}) | PUT B → {(int)allow.StatusCode} | {Describe(afterAllow)}");
        Assert.Equal(HttpStatusCode.NoContent, allow.StatusCode);
        Assert.Equal(BobNewTitle, afterAllow.Title);
        Assert.Equal(DbSeeder.BobId, afterAllow.OwnerUserId);
        Assert.Equal(before.Status, afterAllow.Status);
    }
}
