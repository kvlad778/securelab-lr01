using System.Text.Json;

namespace SecureLab.Api.Tests;

// Це parser для власного A-07; не повний оцінюваний сценарій.
public sealed record ProblemShape(
    string? Type, string? Title, int? Status, string? Detail,
    bool HasType, bool HasTitle, bool HasStatus, bool HasDetail)
{
    public static async Task<ProblemShape> ReadAsync(HttpResponseMessage reply)
    {
        using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
        var root = json.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var value)
            ? value.GetString() : null;
        int? status = root.TryGetProperty("status", out var number)
            ? number.GetInt32() : null;
        return new(Text("type"), Text("title"), status, Text("detail"),
            root.TryGetProperty("type", out _),
            root.TryGetProperty("title", out _),
            root.TryGetProperty("status", out _),
            root.TryGetProperty("detail", out _));
    }
}
