using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureLab.Api.Scaffolding;

namespace SecureLab.Api.Tests;

// ЛР 02: регресійні перевірки безпечного пошуку (S-02, T-04, T-05).
// Ці тести лише читають дані, тому очищення не потрібне.
public sealed class Lab02SearchTests(SecureLabApiFactory factory)
    : IClassFixture<SecureLabApiFactory>
{
    private static readonly Guid UsbIncidentId = Guid.Parse("20000000-0000-0000-0000-000000000005");
    private static readonly Guid OBrienIncidentId = Guid.Parse("20000000-0000-0000-0000-000000000004");

    private readonly HttpClient _client = factory.CreateClient();

    // S-02: той самий контрольний ввід C після виправлення не розширює вибірку.
    [Fact]
    public async Task Search_ControlInputC_Returns200AndEmptyList()
    {
        var items = await SearchAsync("zz-no-match' OR TRUE -- ");

        Assert.Empty(items);
    }

    // Позитивний контроль до S-02 і T-04: звичайний пошук знаходить рівно один запис USB.
    [Fact]
    public async Task Search_Usb_ReturnsExactlyOneSeededIncident()
    {
        var items = await SearchAsync("USB");

        var only = Assert.Single(items);
        Assert.Equal(UsbIncidentId, only.Id);
    }

    // T-04: легітимний апостроф більше не дає 500 і знаходить O'Brien.
    [Fact]
    public async Task Search_LegitimateApostrophe_ReturnsOBrien()
    {
        var items = await SearchAsync("O'Brien");

        var only = Assert.Single(items);
        Assert.Equal(OBrienIncidentId, only.Id);
    }

    // Literal substring: % шукається як звичайний символ (у seed його немає).
    [Fact]
    public async Task Search_PercentSign_IsLiteral()
    {
        var items = await SearchAsync("%");

        Assert.Empty(items);
    }

    // T-05: невідоме sortBy -> 400 Problem Details з ключем sortBy.
    [Fact]
    public async Task Search_UnknownSortBy_Returns400WithSortByKey()
    {
        using var response = await _client.GetAsync("/api/incidents/search?sortBy=price");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("sortBy", out _));
        Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
    }

    private async Task<List<IncidentSearchItemResponse>> SearchAsync(string q)
    {
        using var response = await _client.GetAsync(
            "/api/incidents/search?q=" + Uri.EscapeDataString(q));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = await response.Content.ReadFromJsonAsync<List<IncidentSearchItemResponse>>();
        Assert.NotNull(items);
        return items;
    }
}
