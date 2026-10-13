using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Scaffolding;

// Навчальний старт ЛР 02. Запускати лише з локальними штучними даними.
public static class Lab02Endpoints
{
    public static void MapLab02Endpoints(this WebApplication app)
    {
        app.MapGet("/api/incidents/search", SearchIncidentsAsync);
        app.MapPost("/api/incidents", CreateIncidentAsync);
    }

    // ЛР 02, етап 4: безпечний пошук. Значення q передається в PostgreSQL окремим параметром
    // (LINQ + EF.Functions.ILike), а структуру сортування обирає серверний allowlist.
    private static async Task<IResult> SearchIncidentsAsync(
        string? q,
        string? sortBy,
        SecureLabDbContext db,
        CancellationToken ct)
    {
        // Literal substring: екрануємо \, % і _, потім додаємо зовнішні % для пошуку підрядка.
        var pattern = "%" + EscapeLike(q ?? "") + "%";

        // Одна умова з явним OR: збіг у Title АБО в Description.
        var found = db.Incidents.AsNoTracking()
            .Where(item => EF.Functions.ILike(item.Title, pattern, "\\")
                || EF.Functions.ILike(item.Description, pattern, "\\"));

        // Allowlist sortBy: клієнт лише обирає одну з написаних тут гілок.
        // Ранги замість алфавіту, другий ключ Id робить порядок визначеним.
        IQueryable<Incident>? ordered = sortBy switch
        {
            null or "" or "createdAtUtc" => found
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id),
            "severity" => found
                .OrderBy(item => item.Severity == IncidentSeverity.Critical ? 0
                    : item.Severity == IncidentSeverity.High ? 1
                    : item.Severity == IncidentSeverity.Medium ? 2
                    : 3)
                .ThenBy(item => item.Id),
            "status" => found
                .OrderBy(item => item.Status == IncidentStatus.New ? 0
                    : item.Status == IncidentStatus.Triaged ? 1
                    : item.Status == IncidentStatus.InProgress ? 2
                    : item.Status == IncidentStatus.Resolved ? 3
                    : 4)
                .ThenBy(item => item.Id),
            _ => null
        };

        // Невідоме значення sortBy до SQL не доходить: одразу 400 з ключем sortBy.
        if (ordered is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sortBy"] = ["Допустимі значення: createdAtUtc, severity, status."]
            });
        }

        // Take(50) після сортування стає LIMIT у SQL; Select залишає лише поля response DTO.
        var items = await ordered
            .Take(50)
            .Select(item => new IncidentSearchItemResponse(
                item.Id,
                item.Title,
                item.Description,
                item.Severity.ToString(),
                item.Status.ToString(),
                item.CreatedAtUtc))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    // Спершу подвоюємо сам escape-символ \, потім екрануємо wildcard-и % і _.
    private static string EscapeLike(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    // ЛР 02, етап 2: POST /api/incidents з серверною валідацією контракту 2-A.
    private static async Task<IResult> CreateIncidentAsync(
        CreateIncidentRequest request,
        SecureLabDbContext db,
        CancellationToken ct)
    {
        // Поточний UTC-час сервера фіксуємо один раз на початку обробки.
        var now = DateTimeOffset.UtcNow;
        var errors = new Dictionary<string, string[]>();

        // 1. title: обов'язковий (після Trim), довжину 160 міряємо на вхідному значенні до Trim.
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors["title"] = ["Назва інциденту обов'язкова."];
        }
        else if (request.Title.Length > 160)
        {
            errors["title"] = ["Назва інциденту не довша за 160 символів."];
        }

        // 2. description: обов'язковий (після Trim), не довший за 4000 символів до Trim.
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            errors["description"] = ["Опис інциденту обов'язковий."];
        }
        else if (request.Description.Length > 4000)
        {
            errors["description"] = ["Опис інциденту не довший за 4000 символів."];
        }

        // 3. severity: і перетворення (TryParse), і належність до оголошених значень (IsDefined).
        //    "7" TryParse перетворює на (IncidentSeverity)7, але IsDefined його відхиляє.
        var severityIsValid =
            Enum.TryParse<IncidentSeverity>(request.Severity, ignoreCase: true, out var severity)
            && Enum.IsDefined(severity);
        if (!severityIsValid)
        {
            errors["severity"] = ["Допустимі значення: Low, Medium, High, Critical."];
        }

        // 4. occurredAtUtc: обов'язковий і не пізніше ніж now + 5 хвилин.
        if (request.OccurredAtUtc is null)
        {
            errors["occurredAtUtc"] = ["Час події обов'язковий."];
        }
        else if (request.OccurredAtUtc.Value > now.AddMinutes(5))
        {
            errors["occurredAtUtc"] = ["Час події не може випереджати поточний час сервера більш ніж на 5 хвилин."];
        }

        // 5. Нормалізація: Trim() рівно один раз; далі працюємо лише з цими змінними.
        var title = request.Title?.Trim() ?? "";
        var description = request.Description?.Trim() ?? "";

        // 6. Cross-field (добрий рівень, T-09): для High/Critical опис після Trim() — щонайменше 40 символів.
        //    Виконуємо лише якщо severity вже перевірено і description ще не має власної помилки.
        if (severityIsValid
            && (severity == IncidentSeverity.High || severity == IncidentSeverity.Critical)
            && !errors.ContainsKey("description")
            && description.Length < 40)
        {
            errors["description"] = ["Для severity High або Critical опис має містити щонайменше 40 символів."];
        }

        // 7. Усі помилки контракту — одна відповідь 400 до створення entity та доступу до БД.
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // 8. Предметний конфлікт (добрий рівень, T-03): той самий title (з урахуванням регістру)
        //    у статусі New, Triaged, InProgress або Resolved. Closed повторне створення не блокує.
        var duplicateExists = await db.Incidents.AnyAsync(
            item => item.Title == title
                && (item.Status == IncidentStatus.New
                    || item.Status == IncidentStatus.Triaged
                    || item.Status == IncidentStatus.InProgress
                    || item.Status == IncidentStatus.Resolved),
            ct);
        if (duplicateExists)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Інцидент із такою назвою вже існує",
                detail: "Незакритий інцидент із такою самою назвою вже зареєстровано. Змініть назву або дочекайтеся закриття попереднього.");
        }

        // 9. Entity: server-managed поля (Id, власник, статус, часи) встановлює лише сервер.
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            OwnerUserId = DbSeeder.AliceId,
            Title = title,
            Description = description,
            Severity = severity,
            Status = IncidentStatus.New,
            OccurredAtUtc = request.OccurredAtUtc!.Value.ToUniversalTime(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Incidents.Add(incident);
        await db.SaveChangesAsync(ct);

        // 10. Response DTO: лише поля зовнішнього контракту, без OwnerUserId.
        var response = new CreatedIncidentResponse(
            incident.Id,
            incident.Title,
            incident.Severity.ToString(),
            incident.Status.ToString(),
            incident.OccurredAtUtc,
            incident.CreatedAtUtc);
        return Results.Created($"/api/incidents/{incident.Id}", response);
    }
}

// Request DTO: лише чотири поля, які клієнт має право надсилати.
// Id, OwnerUserId, Status, CreatedAtUtc, UpdatedAtUtc тут навмисно відсутні (захист від overposting).
public sealed record CreateIncidentRequest(
    string? Title, string? Description, string? Severity, DateTimeOffset? OccurredAtUtc);

// Response DTO для 201 Created.
public sealed record CreatedIncidentResponse(
    Guid Id,
    string Title,
    string Severity,
    string Status,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc);

// Response DTO одного елемента пошуку (та сама форма, що й анонімний об'єкт scaffold).
public sealed record IncidentSearchItemResponse(
    Guid Id,
    string Title,
    string Description,
    string Severity,
    string Status,
    DateTimeOffset CreatedAtUtc);
