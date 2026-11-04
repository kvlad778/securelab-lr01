using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Application.Incidents;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;
using SecureLab.Api.Presentation.Contracts;

namespace SecureLab.Api.Presentation.Endpoints;

// ЛР 04 — ВИПРАВЛЕНИЙ СТАН. Політика для чужого головного ресурсу — 404 (як для неіснуючого).
// Порядок кожної mutation: tracked entity → рішення handler-а → (deny: audit / відмова) →
// перевірка контексту → зміна лише дозволених полів → SaveChangesAsync.
public static class IncidentEndpoints
{
    private const string AuthorizationLogCategory = "SecureLab.Authorization";

    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Уся група вимагає verified principal: анонім → 401 (A-01) ще до виконання endpoint.
        var group = endpoints.MapGroup("/api/incidents")
            .WithTags("Incidents")
            .RequireAuthorization();

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

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateIncident")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteIncident")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/triage", TriageAsync)
            .WithName("TriageIncident")
            .Produces<IncidentTriageResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Child resource поза групою — захищається окремо.
        endpoints.MapGet("/api/incident-comments/{id:guid}", GetCommentAsync)
            .WithTags("Incidents")
            .WithName("GetIncidentComment")
            .RequireAuthorization()
            .Produces<IncidentCommentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    // Один helper для чужого й неіснуючого ресурсу: однакові status, Content-Type і body (A-10).
    private static IResult IncidentNotFound() => Results.Problem(
        title: "Інцидент не знайдено",
        statusCode: StatusCodes.Status404NotFound);

    private static IResult CommentNotFound() => Results.Problem(
        title: "Коментар не знайдено",
        statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> GetListAsync(
        string? status,
        HttpContext httpContext,
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

        var userId = CurrentUser.GetId(httpContext.User);
        if (userId is null)
        {
            return Results.Challenge();
        }

        return Results.Ok(await queries.GetListAsync(
            parsedStatus,
            userId.Value,
            CurrentUser.IsAnalyst(httpContext.User),
            cancellationToken));
    }

    private static async Task<IResult> GetDetailsAsync(
        Guid id,
        HttpContext httpContext,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUser.GetId(httpContext.User);
        if (userId is null)
        {
            return Results.Challenge();
        }

        var incident = await queries.GetDetailsAsync(
            id,
            userId.Value,
            CurrentUser.IsAnalyst(httpContext.User),
            cancellationToken);

        // Чужий і неіснуючий → той самий IncidentNotFound(); detail з id прибрано.
        return incident is null ? IncidentNotFound() : Results.Ok(incident);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateIncidentRequest request,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        // Tracked entity (без AsNoTracking), інакше SaveChangesAsync нічого не запише.
        var incident = await db.Incidents.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident is null)
        {
            return IncidentNotFound();
        }

        var canRead = await authorizationService.AuthorizeAsync(
            httpContext.User, incident, IncidentOperations.Read);
        if (!canRead.Succeeded)
        {
            return IncidentNotFound(); // чужий ресурс: як неіснуючий
        }

        var canUpdate = await authorizationService.AuthorizeAsync(
            httpContext.User, incident, IncidentOperations.Update);
        if (!canUpdate.Succeeded)
        {
            return Results.Forbid(); // ресурс видно (Analyst), але змінювати content не можна → 403
        }

        var errors = ValidateUpdate(request, incident.Severity);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Змінюються лише content fields. Owner/status/severity/id з JSON не читаються взагалі.
        incident.Title = request.Title!.Trim();
        incident.Description = request.Description!.Trim();
        incident.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        SecureLabDbContext db,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident is null)
        {
            return IncidentNotFound();
        }

        var canRead = await authorizationService.AuthorizeAsync(
            httpContext.User, incident, IncidentOperations.Read);
        if (!canRead.Succeeded)
        {
            return IncidentNotFound();
        }

        var canDelete = await authorizationService.AuthorizeAsync(
            httpContext.User, incident, IncidentOperations.Delete);
        if (!canDelete.Succeeded)
        {
            return Results.Forbid(); // власник, але статус уже не New; або Analyst
        }

        db.Incidents.Remove(incident);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> TriageAsync(
        Guid id,
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        SecureLabDbContext db,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (incident is null)
        {
            return IncidentNotFound();
        }

        var canRead = await authorizationService.AuthorizeAsync(
            httpContext.User, incident, IncidentOperations.Read);
        if (!canRead.Succeeded)
        {
            return IncidentNotFound();
        }

        // Рішення ухвалюється всередині endpoint (не декларативною policy), щоб записати audit відмови.
        var canTriage = await authorizationService.AuthorizeAsync(
            httpContext.User, incident, IncidentOperations.Triage);
        if (!canTriage.Succeeded)
        {
            AuditDenied(httpContext, loggers.CreateLogger(AuthorizationLogCategory), incident.Id, "incident.triage");
            return Results.Forbid();
        }

        // Контекст: дозволено лише New → Triaged. Поточний status читаємо з БД, не з запиту.
        if (incident.Status != IncidentStatus.New)
        {
            return Results.Problem(
                title: "Перехід недоступний",
                detail: "Triage дозволено лише для інциденту в статусі New.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var analystId = CurrentUser.GetId(httpContext.User)!.Value;
        var now = DateTimeOffset.UtcNow;
        db.IncidentStatusHistory.Add(new IncidentStatusHistory
        {
            Id = Guid.NewGuid(),
            IncidentId = incident.Id,
            ChangedByUserId = analystId,
            OldStatus = incident.Status,
            NewStatus = IncidentStatus.Triaged,
            Note = "Первинну оцінку виконано через API.",
            CreatedAtUtc = now
        });

        // Наступний статус визначає сервер; у запиті немає поля status.
        incident.Status = IncidentStatus.Triaged;
        incident.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new IncidentTriageResponse(incident.Id, incident.Status.ToString()));
    }

    private static async Task<IResult> GetCommentAsync(
        Guid id,
        HttpContext httpContext,
        IncidentQueries queries,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUser.GetId(httpContext.User);
        if (userId is null)
        {
            return Results.Challenge();
        }

        var comment = await queries.GetCommentAsync(
            id,
            userId.Value,
            CurrentUser.IsAnalyst(httpContext.User),
            cancellationToken);

        return comment is null ? CommentNotFound() : Results.Ok(comment);
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

    // Ті самі правила змісту, що й у create ЛР 2 (title ≤ 160, description ≤ 4000, High/Critical — ≥ 40).
    private static Dictionary<string, string[]> ValidateUpdate(
        UpdateIncidentRequest request,
        IncidentSeverity severity)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors["title"] = ["Назва інциденту обов'язкова."];
        }
        else if (request.Title.Length > 160)
        {
            errors["title"] = ["Назва інциденту не довша за 160 символів."];
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            errors["description"] = ["Опис інциденту обов'язковий."];
        }
        else if (request.Description.Length > 4000)
        {
            errors["description"] = ["Опис інциденту не довший за 4000 символів."];
        }
        else if ((severity == IncidentSeverity.High || severity == IncidentSeverity.Critical)
            && request.Description.Trim().Length < 40)
        {
            errors["description"] = ["Для severity High або Critical опис має містити щонайменше 40 символів."];
        }

        return errors;
    }

    // Безпечна аудиторська подія: лише server-verified subject, дія, ресурс, результат, час сервера
    // і той самий trace id, що потрапляє в traceId тіла відповіді 403. Без cookie/password/token/body.
    private static void AuditDenied(HttpContext httpContext, ILogger logger, Guid incidentId, string action)
    {
        logger.LogWarning(
            "authorization.denied subject={SubjectId} action={Action} "
            + "resource={ResourceId} result={Result} "
            + "occurredAtUtc={OccurredAtUtc:O} traceId={TraceId}",
            CurrentUser.GetId(httpContext.User),
            action,
            incidentId,
            "denied",
            DateTimeOffset.UtcNow,
            Activity.Current?.Id ?? httpContext.TraceIdentifier);
    }
}
