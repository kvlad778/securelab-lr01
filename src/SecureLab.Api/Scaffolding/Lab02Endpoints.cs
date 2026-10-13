using Microsoft.EntityFrameworkCore;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Scaffolding;

// Навчальний старт ЛР 02. Запускати лише з локальними штучними даними.
public static class Lab02Endpoints
{
    public static void MapLab02Endpoints(this WebApplication app)
    {
        app.MapGet("/api/incidents/search", async (string? q, string? sortBy, SecureLabDbContext db, CancellationToken ct) =>
        {
            var order = sortBy switch
            {
                null or "" or "createdAtUtc" => "created_at_utc DESC",
                "severity" => "severity", "status" => "status", _ => sortBy
            };
            var sql = "SELECT * FROM incidents WHERE title ILIKE '%" + (q ?? "")
                + "%' OR description ILIKE '%" + (q ?? "") + "%' ORDER BY " + order + " LIMIT 50";
            var rows = await db.Incidents.FromSqlRaw(sql).AsNoTracking().ToListAsync(ct);
            return Results.Ok(rows.Select(row => new
            {
                row.Id, row.Title, row.Description,
                Severity = row.Severity.ToString(), Status = row.Status.ToString(), row.CreatedAtUtc
            }));
        });
        app.MapPost("/api/incidents", CreateIncidentAsync);
    }

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
