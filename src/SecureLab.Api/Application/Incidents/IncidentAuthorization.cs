using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Application.Incidents;

// ЛР 04: операції над Incident, для яких сервер ухвалює рішення «суб'єкт + дія + ресурс + контекст».
public static class IncidentOperations
{
    public static readonly OperationAuthorizationRequirement Read = new() { Name = nameof(Read) };
    public static readonly OperationAuthorizationRequirement Update = new() { Name = nameof(Update) };
    public static readonly OperationAuthorizationRequirement Delete = new() { Name = nameof(Delete) };
    public static readonly OperationAuthorizationRequirement Triage = new() { Name = nameof(Triage) };
}

// Єдине джерело ідентичності й ролі — verified principal (HttpContext.User), сформований під час login.
// Значення з body, query, URL чи заголовків сюди не потрапляють.
public static class CurrentUser
{
    public static Guid? GetId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static bool IsAnalyst(ClaimsPrincipal user) => user.IsInRole(DbSeeder.AnalystRole);
}

// Централізоване правило матриці docs/access-matrix.md для завантаженого Incident.
// Read повторено в query scoping (IncidentQueries) тим самим предикатом: owner АБО Analyst.
public sealed class IncidentAuthorizationHandler
    : AuthorizationHandler<OperationAuthorizationRequirement, Incident>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement,
        Incident incident)
    {
        var userId = CurrentUser.GetId(context.User);
        var isOwner = userId is not null && userId.Value == incident.OwnerUserId;
        var isAnalyst = CurrentUser.IsAnalyst(context.User);

        var allowed = requirement.Name switch
        {
            // Перегляд: власник або Analyst (для аналізу). Administrator — лише як власник.
            nameof(IncidentOperations.Read) => isOwner || isAnalyst,
            // Зміна змісту: лише власник. Read-дозвіл Analyst не переноситься на update.
            nameof(IncidentOperations.Update) => isOwner,
            // Вилучення: лише власник і лише поки інцидент у статусі New (ще не взятий в аналіз).
            nameof(IncidentOperations.Delete) => isOwner && incident.Status == IncidentStatus.New,
            // Privileged transition: лише роль Analyst; допустимість поточного status перевіряє endpoint (409).
            nameof(IncidentOperations.Triage) => isAnalyst,
            _ => false
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
