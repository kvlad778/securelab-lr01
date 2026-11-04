using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Application.Incidents;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;
using SecureLab.Api.Presentation.Contracts;

namespace SecureLab.Api.Presentation.Endpoints;

// ЛР 04 — КОНТРОЛЬОВАНИЙ ВРАЗЛИВИЙ СТАН (vulnerable commit). Лише локальний стенд, штучні дані.
// 1) GET /api/incidents/{id} шукає інцидент тільки за id (стан після ЛР 3, нічого не змінено).
// 2) PUT /api/incidents/{id} завантажує ресурс лише за id, без перевірки власника (горизонтальний IDOR).
// 3) POST /api/incidents/{id}/triage не перевіряє роль Analyst (вертикальне підвищення привілеїв).
public static class IncidentEndpoints
{
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/incidents")
            .WithTags("Incidents");

        group.MapGet("/", GetListAsync)
            .WithName("GetIncidents")
            .Produces<IReadOnlyList<IncidentListItemResponse>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", GetDetailsAsync)
            .WithName("GetIncidentDetails")
            .Produces<IncidentDetailsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/severity-summary", GetSeveritySummaryAsync)
            .WithName("GetIncidentSeveritySummary")
            .Produces<IReadOnlyList<IncidentSeveritySummaryResponse>>()
            .ProducesValidationProblem();

        // VULNERABLE: потрібен login, але немає ownership/policy decision.
        group.MapPut("/{id:guid}", UpdateVulnerableAsync).RequireAuthorization();
        group.MapPost("/{id:guid}/triage", TriageVulnerableAsync).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> GetListAsync(
        string? status,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        IncidentStatus? parsedStatus = null;
        if (status is not null)
        {
            if (!Enum.TryParse<IncidentStatus>(status, ignoreCase: true, out var candidate)
                || !Enum.IsDefined(candidate))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Допустимі значення: New, Triaged, InProgress, Resolved, Closed."]
                });
            }

            parsedStatus = candidate;
        }

        return Results.Ok(await queries.GetListAsync(parsedStatus, cancellationToken));
    }

    private static async Task<IResult> GetDetailsAsync(
        Guid id,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var incident = await queries.GetDetailsAsync(id, cancellationToken);
        return incident is null
            ? Results.Problem(
                title: "Інцидент не знайдено",
                detail: $"Інцидент '{id}' не існує.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(incident);
    }

    // VULNERABLE (горизонтальний IDOR): пошук лише за id → зміна → без порівняння з principal.
    private static async Task<IResult> UpdateVulnerableAsync(
        Guid id,
        UpdateIncidentRequest request,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident is null)
        {
            return Results.NotFound();
        }

        incident.Title = request.Title?.Trim() ?? incident.Title;
        incident.Description = request.Description?.Trim() ?? incident.Description;
        incident.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // VULNERABLE (вертикальне підвищення): будь-який автентифікований користувач виконує triage.
    private static async Task<IResult> TriageVulnerableAsync(
        Guid id,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident is null)
        {
            return Results.NotFound();
        }

        incident.Status = IncidentStatus.Triaged;
        incident.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new IncidentTriageResponse(incident.Id, incident.Status.ToString()));
    }

    private static async Task<IResult> GetSeveritySummaryAsync(
        string? status,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        IncidentStatus? parsedStatus = null;
        if (status is not null)
        {
            // Дозволені лише назви статусів із цього списку
            var allowedName = Enum.GetNames<IncidentStatus>()
                .FirstOrDefault(name => string.Equals(name, status, StringComparison.OrdinalIgnoreCase));

            if (allowedName is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Допустимі значення: New, Triaged, InProgress, Resolved, Closed."]
                });
            }

            parsedStatus = Enum.Parse<IncidentStatus>(allowedName);
        }

        var summary = await queries.GetSeveritySummaryAsync(parsedStatus, cancellationToken);
        return Results.Ok(summary);
    }
}
