using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;
using static SecureLab.Api.Tests.Lab03TestSupport;

namespace SecureLab.Api.Tests;

// Власні authorization-сценарії ЛР 04 (добрий рівень, політика 404 для чужого ресурсу).
// Один користувач — один HttpClient зі своєю cookie. Seed-ресурси негативні тести не змінюють;
// позитивні mutation-тести працюють лише з інцидентом, який самі створили, і прибирають його у finally.
public sealed class Lab04AuthorizationTests(SecureLabApiFactory factory) : IClassFixture<SecureLabApiFactory>
{
    private const string BobIncidentTitle = "Невідома спроба входу";
    private const string BobCommentText = "Розпочато перевірку журналу автентифікації.";
    private const string InternalCommentText = "Внутрішня примітка аналітика: перевірити домен відправника.";

    private Task<HttpClient> LoginAsync(string userName) => factory.LoginAsync(userName, StudyPassword);

    // State assertion: фактичний стан із БД (owner/status API назовні не віддає).
    private async Task<Incident?> ReadIncidentAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        return await db.Incidents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);
    }

    private async Task<int> CountHistoryAsync(Guid incidentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecureLabDbContext>();
        return await db.IncidentStatusHistory.CountAsync(item => item.IncidentId == incidentId);
    }

    private static async Task<Guid> CreateOwnIncidentAsync(HttpClient client, string title)
    {
        using var response = await client.PostAsJsonAsync("/api/incidents", IncidentBody(title));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadCreatedIdAsync(response);
    }

    private static async Task<JsonObject> ReadProblemWithoutTraceAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Remove("traceId");   // єдине дозволене поле кореляції
        body.Remove("requestId");
        return body;
    }

    // A-01 (добрий): анонімний details → 401 Problem Details, не HTML redirect.
    [Fact]
    public async Task A01_AnonymousDetails_Returns401WithoutRedirect()
    {
        using var anonymous = NewHttpsClient(factory);

        using var response = await anonymous.GetAsync($"/api/incidents/{DbSeeder.AliceIncidentId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Підозрілий лист", await response.Content.ReadAsStringAsync());
    }

    // A-01 (добрий): анонімна mutation теж 401 і стан не змінюється.
    [Fact]
    public async Task A01_AnonymousUpdate_Returns401_AndDoesNotChangeIncident()
    {
        var before = await ReadIncidentAsync(DbSeeder.AliceIncidentId);
        using var anonymous = NewHttpsClient(factory);

        using var response = await anonymous.PutAsJsonAsync(
            $"/api/incidents/{DbSeeder.AliceIncidentId}",
            new { title = "Anonymous change", description = "Анонімна спроба зміни змісту інциденту." });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = await ReadIncidentAsync(DbSeeder.AliceIncidentId);
        Assert.Equal(before!.Title, after!.Title);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
    }

    // A-02 (достатній): owner → allow; лише поля response DTO.
    [Fact]
    public async Task A02_OwnerReadsOwnIncident_Returns200WithSafeFields()
    {
        using var alice = await LoginAsync("alice");

        using var response = await alice.GetAsync($"/api/incidents/{DbSeeder.AliceIncidentId}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DbSeeder.AliceIncidentId, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Аліса Коваль", json.RootElement.GetProperty("ownerDisplayName").GetString());
        Assert.False(json.RootElement.TryGetProperty("ownerUserId", out _));
    }

    // A-03 (достатній): authenticated non-owner → deny (404 як для неіснуючого), даних Bob немає.
    [Fact]
    public async Task A03_NonOwnerReadsForeignIncident_Returns404WithoutBobData()
    {
        using var alice = await LoginAsync("alice");

        using var response = await alice.GetAsync($"/api/incidents/{DbSeeder.BobIncidentId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(BobIncidentTitle, body);
        Assert.DoesNotContain("Боб Мельник", body);
        Assert.DoesNotContain(DbSeeder.BobIncidentId.ToString(), body);
    }

    // A-03R (entry-контракт ЛР 5): Analyst читає інцидент Alice для аналізу.
    [Fact]
    public async Task A03R_AnalystReadsAliceIncident_Returns200()
    {
        using var morgan = await LoginAsync("morgan");

        using var response = await morgan.GetAsync($"/api/incidents/{DbSeeder.AliceIncidentId}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DbSeeder.AliceIncidentId, json.RootElement.GetProperty("id").GetGuid());
    }

    // A-03R: read-дозвіл Analyst не дає права довільної mutation чужого content.
    [Fact]
    public async Task A03R_AnalystUpdateAliceIncident_Returns403_AndDoesNotChangeIncident()
    {
        var before = await ReadIncidentAsync(DbSeeder.AliceIncidentId);
        using var morgan = await LoginAsync("morgan");

        using var response = await morgan.PutAsJsonAsync(
            $"/api/incidents/{DbSeeder.AliceIncidentId}",
            new { title = "Analyst change", description = "Спроба Analyst змінити зміст чужого інциденту." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await ReadIncidentAsync(DbSeeder.AliceIncidentId);
        Assert.Equal(before!.Title, after!.Title);
        Assert.Equal(before.Description, after.Description);
    }

    // A-04 (добрий): Alice змінює інцидент Bob → 404, стан Bob без змін.
    [Fact]
    public async Task A04_NonOwnerUpdate_Returns404_AndDoesNotChangeBobIncident()
    {
        var before = await ReadIncidentAsync(DbSeeder.BobIncidentId);
        using var alice = await LoginAsync("alice");

        using var response = await alice.PutAsJsonAsync(
            $"/api/incidents/{DbSeeder.BobIncidentId}",
            new { title = "Changed by Alice", description = "Спроба Alice змінити зміст чужого інциденту Bob." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await ReadIncidentAsync(DbSeeder.BobIncidentId);
        Assert.Equal(before!.Title, after!.Title);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);

        // Bob як власник і далі бачить свій незмінений запис.
        using var bob = await LoginAsync("bob");
        using var bobRead = await bob.GetAsync($"/api/incidents/{DbSeeder.BobIncidentId}");
        Assert.Contains(BobIncidentTitle, await bobRead.Content.ReadAsStringAsync());
    }

    // A-04 (добрий): Alice видаляє інцидент Bob → 404, запис існує.
    [Fact]
    public async Task A04_NonOwnerDelete_Returns404_AndBobIncidentStillExists()
    {
        using var alice = await LoginAsync("alice");

        using var response = await alice.DeleteAsync($"/api/incidents/{DbSeeder.BobIncidentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await ReadIncidentAsync(DbSeeder.BobIncidentId));
    }

    // Позитивна регресія mutation: власник змінює зміст власного інциденту.
    [Fact]
    public async Task Update_AsOwner_Returns204_AndChangesContent()
    {
        var title = "L04-UPD " + UniqueSuffix();
        var newTitle = title + " edited";
        try
        {
            using var alice = await LoginAsync("alice");
            var id = await CreateOwnIncidentAsync(alice, title);

            using var response = await alice.PutAsJsonAsync(
                $"/api/incidents/{id}",
                new { title = newTitle, description = "Оновлений зміст власного навчального інциденту." });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var after = await ReadIncidentAsync(id);
            Assert.Equal(newTitle, after!.Title);
        }
        finally
        {
            await DeleteIncidentsByTitleAsync(factory, title);
            await DeleteIncidentsByTitleAsync(factory, newTitle);
        }
    }

    // Позитивна регресія delete: власник вилучає власний New-інцидент.
    [Fact]
    public async Task Delete_AsOwnerOfNewIncident_Returns204()
    {
        var title = "L04-DEL " + UniqueSuffix();
        try
        {
            using var alice = await LoginAsync("alice");
            var id = await CreateOwnIncidentAsync(alice, title);

            using var response = await alice.DeleteAsync($"/api/incidents/{id}");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Null(await ReadIncidentAsync(id));
        }
        finally
        {
            await DeleteIncidentsByTitleAsync(factory, title);
        }
    }

    // A-05 (добрий): list Alice — лише дозволена множина (власні), без записів Bob.
    [Fact]
    public async Task A05_ListAsAlice_ContainsOnlyOwnIncidents()
    {
        using var alice = await LoginAsync("alice");

        using var response = await alice.GetAsync("/api/incidents");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(DbSeeder.AliceIncidentId, ids);
        Assert.DoesNotContain(DbSeeder.BobIncidentId, ids);
        Assert.DoesNotContain(Guid.Parse("20000000-0000-0000-0000-000000000004"), ids);
        foreach (var id in ids)
        {
            Assert.Equal(DbSeeder.AliceId, (await ReadIncidentAsync(id))!.OwnerUserId);
        }
    }

    // A-06 (добрий): Reporter виконує privileged transition → 403, status без змін, історії немає.
    [Fact]
    public async Task A06_ReporterTriage_Returns403_AndStatusStaysNew()
    {
        var title = "L04-A06 " + UniqueSuffix();
        try
        {
            using var alice = await LoginAsync("alice");
            var id = await CreateOwnIncidentAsync(alice, title);

            using var response = await alice.PostAsync($"/api/incidents/{id}/triage", content: null);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(IncidentStatus.New, (await ReadIncidentAsync(id))!.Status);
            Assert.Equal(0, await CountHistoryAsync(id));
        }
        finally
        {
            await DeleteIncidentsByTitleAsync(factory, title);
        }
    }

    // A-07 (добрий): Analyst + New → allow; next status визначає сервер; повтор → 409.
    [Fact]
    public async Task A07_AnalystTriageNewIncident_Returns200_AndServerSetsTriaged()
    {
        var title = "L04-A07 " + UniqueSuffix();
        try
        {
            using var alice = await LoginAsync("alice");
            var id = await CreateOwnIncidentAsync(alice, title);
            using var morgan = await LoginAsync("morgan");

            using var response = await morgan.PostAsync($"/api/incidents/{id}/triage", content: null);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Triaged", json.RootElement.GetProperty("status").GetString());
            var after = await ReadIncidentAsync(id);
            Assert.Equal(IncidentStatus.Triaged, after!.Status);
            Assert.Equal(DbSeeder.AliceId, after.OwnerUserId);
            Assert.Equal(1, await CountHistoryAsync(id));

            using var repeat = await morgan.PostAsync($"/api/incidents/{id}/triage", content: null);
            Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        }
        finally
        {
            await DeleteIncidentsByTitleAsync(factory, title);
        }
    }

    // A-08a (добрий): forged owner/status/severity/role/id у regular update ігноруються;
    // дозволений content змінюється, protected state — ні.
    [Fact]
    public async Task A08a_UpdateWithForgedProtectedFields_ChangesOnlyContent()
    {
        var title = "L04-A08 " + UniqueSuffix();
        var newTitle = title + " content";
        try
        {
            using var alice = await LoginAsync("alice");
            var id = await CreateOwnIncidentAsync(alice, title);

            using var response = await alice.PutAsJsonAsync($"/api/incidents/{id}", new
            {
                title = newTitle,
                description = "Дозволена зміна змісту з підробленими захищеними полями.",
                ownerUserId = DbSeeder.BobId,
                status = "Closed",
                severity = "Critical",
                role = DbSeeder.AnalystRole,
                id = Guid.NewGuid()
            });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var after = await ReadIncidentAsync(id);
            Assert.NotNull(after);
            Assert.Equal(newTitle, after.Title);
            Assert.Equal(DbSeeder.AliceId, after.OwnerUserId);
            Assert.Equal(IncidentStatus.New, after.Status);
            Assert.Equal(IncidentSeverity.Low, after.Severity);
        }
        finally
        {
            await DeleteIncidentsByTitleAsync(factory, title);
            await DeleteIncidentsByTitleAsync(factory, newTitle);
        }
    }

    // A-10 (добрий, політика 404): чужий і неіснуючий id тим самим клієнтом нерозрізнювані.
    [Fact]
    public async Task A10_ForeignAndUnknownId_ReturnSame404()
    {
        using var alice = await LoginAsync("alice");

        using var foreign = await alice.GetAsync($"/api/incidents/{DbSeeder.BobIncidentId}");
        using var unknown = await alice.GetAsync($"/api/incidents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(unknown.StatusCode, foreign.StatusCode);
        Assert.Equal(
            unknown.Content.Headers.ContentType?.ToString(),
            foreign.Content.Headers.ContentType?.ToString());
        var foreignBody = await ReadProblemWithoutTraceAsync(foreign);
        var unknownBody = await ReadProblemWithoutTraceAsync(unknown);
        Assert.True(JsonNode.DeepEquals(unknownBody, foreignBody));
    }

    // A-12 (добрий): Reporter не читає коментар до чужого інциденту за прямим id.
    [Fact]
    public async Task A12_ReporterReadsForeignCommentDirectly_Returns404()
    {
        using var alice = await LoginAsync("alice");

        using var response = await alice.GetAsync($"/api/incident-comments/{DbSeeder.MorganToBobCommentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(BobCommentText, await response.Content.ReadAsStringAsync());
    }

    // A-12 (добрий): internal comment не потрапляє у відповідь Reporter (ні details, ні прямий id);
    // Analyst бачить його після авторизації parent incident.
    [Fact]
    public async Task A12_InternalComment_HiddenFromReporter_VisibleToAnalyst()
    {
        using var alice = await LoginAsync("alice");
        using var morgan = await LoginAsync("morgan");

        using var aliceDetails = await alice.GetAsync($"/api/incidents/{DbSeeder.AliceIncidentId}");
        using var aliceDirect = await alice.GetAsync($"/api/incident-comments/{DbSeeder.InternalAliceCommentId}");
        using var morganDetails = await morgan.GetAsync($"/api/incidents/{DbSeeder.AliceIncidentId}");
        using var morganDirect = await morgan.GetAsync($"/api/incident-comments/{DbSeeder.InternalAliceCommentId}");

        Assert.Equal(HttpStatusCode.OK, aliceDetails.StatusCode);
        Assert.DoesNotContain(InternalCommentText, await aliceDetails.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, aliceDirect.StatusCode);
        Assert.Contains(InternalCommentText, await morganDetails.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, morganDirect.StatusCode);
    }

    // A-12: власник читає не-internal коментар до власного інциденту (позитивна регресія child).
    [Fact]
    public async Task A12_OwnerReadsPublicCommentOfOwnIncident_Returns200()
    {
        using var alice = await LoginAsync("alice");

        using var response = await alice.GetAsync($"/api/incident-comments/{DbSeeder.MorganToAliceCommentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
