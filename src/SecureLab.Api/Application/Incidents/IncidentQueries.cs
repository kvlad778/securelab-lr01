using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;
using SecureLab.Api.Presentation.Contracts;

namespace SecureLab.Api.Application.Incidents;

public sealed class IncidentQueries(SecureLabDbContext dbContext, ILogger<IncidentQueries> logger)
{
    // ЛР 04 (A-05): query scoping — Reporter бачить лише власні, Analyst — усі для аналізу.
    // userId і canSeeAll обчислює endpoint із verified principal і передає явними параметрами.
    public async Task<IReadOnlyList<IncidentListItemResponse>> GetListAsync(
        IncidentStatus? status,
        Guid userId,
        bool canSeeAll,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading incidents with status filter {Status}", status);

        var query = dbContext.Incidents
            .AsNoTracking()
            .Where(incident => canSeeAll || incident.OwnerUserId == userId);

        if (status is not null)
        {
            query = query.Where(incident => incident.Status == status);
        }

        return await query
            .OrderByDescending(incident => incident.CreatedAtUtc)
            .Select(incident => new IncidentListItemResponse(
                incident.Id,
                incident.Title,
                incident.Severity.ToString(),
                incident.Status.ToString(),
                incident.OccurredAtUtc,
                incident.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    // ЛР 04 (A-02/A-03/A-03R): політика 404 — id і правило доступу в одному Where,
    // тому чужий і неіснуючий id дають однаковий null. Internal comment бачить лише Analyst.
    public Task<IncidentDetailsResponse?> GetDetailsAsync(
        Guid id,
        Guid userId,
        bool isAnalyst,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading incident {IncidentId}", id);

        return dbContext.Incidents
            .AsNoTracking()
            .Where(incident => incident.Id == id
                && (isAnalyst || incident.OwnerUserId == userId))
            .Select(incident => new IncidentDetailsResponse(
                incident.Id,
                incident.Title,
                incident.Description,
                incident.Severity.ToString(),
                incident.Status.ToString(),
                incident.OccurredAtUtc,
                incident.CreatedAtUtc,
                incident.Owner.DisplayName,
                incident.Comments
                    .Where(comment => isAnalyst || !comment.IsInternal)
                    .OrderBy(comment => comment.CreatedAtUtc)
                    .Select(comment => new IncidentCommentResponse(
                        comment.Id,
                        comment.Author.DisplayName,
                        comment.Text,
                        comment.CreatedAtUtc))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    // ЛР 04 (A-12): child resource через parent. Умова доступу до батьківського інциденту
    // і правило internal comment стоять у тому самому запиті (без окремого пошуку лише за id коментаря).
    public Task<IncidentCommentResponse?> GetCommentAsync(
        Guid commentId,
        Guid userId,
        bool isAnalyst,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading incident comment {CommentId}", commentId);

        return dbContext.IncidentComments
            .AsNoTracking()
            .Where(comment => comment.Id == commentId
                && (isAnalyst
                    || (comment.Incident.OwnerUserId == userId && !comment.IsInternal)))
            .Select(comment => new IncidentCommentResponse(
                comment.Id,
                comment.Author.DisplayName,
                comment.Text,
                comment.CreatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
    }

    // Без змін з ЛР 1. Обмеження за principal для severity-summary — відмінний рівень (A-11).
    public async Task<IReadOnlyList<IncidentSeveritySummaryResponse>> GetSeveritySummaryAsync(
        IncidentStatus? status,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Incidents.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(incident => incident.Status == status);
        }

        // 1. База групує інциденти за важливістю й рахує їх
        var groups = await query
            .GroupBy(incident => incident.Severity)
            .Select(group => new { Severity = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // 2. Сортуємо вже в пам'яті: більша кількість першою, при рівній кількості за назвою від A до Z
        var summary = groups
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Severity.ToString(), StringComparer.Ordinal)
            .Select(item => new IncidentSeveritySummaryResponse(
                item.Severity.ToString(),
                item.Count))
            .ToList();

        logger.LogInformation(
            "Built severity summary with {GroupCount} groups for status filter {Status}",
            summary.Count,
            status);

        return summary;
    }
}
