namespace SecureLab.Api.Presentation.Contracts;

public sealed record IncidentListItemResponse(
    Guid Id,
    string Title,
    string Severity,
    string Status,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record IncidentDetailsResponse(
    Guid Id,
    string Title,
    string Description,
    string Severity,
    string Status,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc,
    string OwnerDisplayName,
    IReadOnlyList<IncidentCommentResponse> Comments);

public sealed record IncidentCommentResponse(
    Guid Id,
    string AuthorDisplayName,
    string Text,
    DateTimeOffset CreatedAtUtc);

public sealed record IncidentSeveritySummaryResponse(
    string Severity,
    int Count);

// ЛР 04: звичайне оновлення змісту. Лише content fields:
// OwnerUserId, Status, Severity, Id, role тут навмисно відсутні (захист від overposting, A-08a).
public sealed record UpdateIncidentRequest(
    string? Title,
    string? Description);

// ЛР 04: відповідь на POST /api/incidents/{id}/triage — новий статус визначив сервер.
public sealed record IncidentTriageResponse(
    Guid Id,
    string Status);
