using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using SecureLab.Api.Data;
using SecureLab.Api.Data.Entities;

namespace SecureLab.Api.Scaffolding;

public static class Lab03Identity
{
    // ЛР 03 (добрий рівень): назва політики обмеження спроб входу.
    public const string LoginRateLimitPolicy = "login";

    // Категорія журналу для подій автентифікації (без credentials).
    private const string AuthLogCategory = "SecureLab.Auth";

    // Контракт register: межі взято з моделі (DisplayName = 120, UserName/Email = 256).
    private const int UserNameMinLength = 3;
    private const int UserNameMaxLength = 64;
    private const int EmailMaxLength = 256;
    private const int DisplayNameMaxLength = 120;
    private const int PasswordMaxLength = 128;

    // Дозволені символи імені користувача: латинські літери, цифри, крапка, дефіс, підкреслення.
    private static readonly Regex UserNamePattern = new(
        "^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private const string PasswordPolicyMessage =
        "Пароль: щонайменше 10 символів, велика й мала латинські літери, цифра та символ, що не є літерою чи цифрою.";

    public static void AddLab03Identity(this WebApplicationBuilder builder)
    {
        builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 10;
        }).AddEntityFrameworkStores<SecureLabDbContext>().AddDefaultTokenProviders();
        builder.Services.AddAuthorization();
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        // ЛР 03, добрий рівень: обмеження частоти спроб входу.
        // Ключ групи (partition key) — IP-адреса клієнта; ліміт і вікно читаються з конфігурації
        // під час запиту (за замовчуванням 5 спроб за 60 секунд), щоб тести могли задати свої значення.
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>().CreateLogger(AuthLogCategory);
                logger.LogWarning(
                    "Auth event {EventType}: result {Result}, trace {TraceId}",
                    "login", "rate_limited", context.HttpContext.TraceIdentifier);
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(LoginRateLimitPolicy, httpContext =>
            {
                var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
                var permitLimit = configuration.GetValue("LoginRateLimit:PermitLimit", 5);
                var windowSeconds = configuration.GetValue("LoginRateLimit:WindowSeconds", 60);
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromSeconds(windowSeconds),
                        QueueLimit = 0
                    });
            });
        });
    }

    public static void MapLab03Identity(this WebApplication app)
    {
        app.MapPost("/api/auth/register", RegisterAsync);

        app.MapPost("/api/auth/login", async (
            LoginInput input,
            SignInManager<ApplicationUser> signIn,
            HttpContext httpContext,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger(AuthLogCategory);
            if (string.IsNullOrWhiteSpace(input.UserName) || string.IsNullOrEmpty(input.Password))
            {
                LogAuthEvent(logger, "login", "failure", null, httpContext);
                return Results.Unauthorized();
            }

            var result = await signIn.PasswordSignInAsync(input.UserName, input.Password, false, false);
            if (!result.Succeeded)
            {
                // Однаковий 401 для невідомого користувача й хибного пароля (A-07).
                // Введене ім'я в журнал не пишемо: туди помилково може потрапити пароль.
                LogAuthEvent(logger, "login", "failure", null, httpContext);
                return Results.Unauthorized();
            }

            var user = await signIn.UserManager.FindByNameAsync(input.UserName);
            LogAuthEvent(logger, "login", "success", user?.Id, httpContext);
            return Results.NoContent();
        }).RequireRateLimiting(LoginRateLimitPolicy);

        // ЛР 03, добрий рівень: стандартний вихід Identity (sign-out) замість заглушки 501.
        app.MapPost("/api/auth/logout", async (
            SignInManager<ApplicationUser> signIn,
            HttpContext httpContext,
            ILoggerFactory loggers) =>
        {
            Guid? userId = Guid.TryParse(
                httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsedId)
                ? parsedId : null;
            await signIn.SignOutAsync();
            LogAuthEvent(loggers.CreateLogger(AuthLogCategory), "logout", "success", userId, httpContext);
            return Results.NoContent();
        }).RequireAuthorization();

        // ЛР 03: /api/me читає користувача лише з verified principal (HttpContext.User).
        // Заголовок X-Demo-UserId більше ніде не читається.
        app.MapGet("/api/me", GetCurrentUser).RequireAuthorization();

        // Приклад лише mechanics: не повертає identity і не реалізує оцінювану operation.
        app.MapGet("/api/auth/session-check", () => Results.NoContent()).RequireAuthorization();
    }

    private static IResult GetCurrentUser(HttpContext httpContext)
    {
        var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userId, out var verifiedUserId))
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        var userName = httpContext.User.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
        var role = httpContext.User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty;

        return Results.Ok(new CurrentUserResponse(verifiedUserId, userName, role));
    }

    private static async Task<IResult> RegisterAsync(
        RegistrationInput input,
        UserManager<ApplicationUser> users,
        HttpContext httpContext,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(AuthLogCategory);

        // 1. Перевірки зовнішнього контракту (обов'язковість, довжина, формат).
        // 2. Нормалізація: лише Trim() для userName, email, displayName; password не змінюється.
        var errors = ValidateRegistration(input, out var userName, out var email, out var displayName);
        if (errors.Count > 0)
        {
            LogAuthEvent(logger, "register", "rejected_validation", null, httpContext);
            return Results.ValidationProblem(errors);
        }

        // 3–4. Hash обчислює Identity всередині CreateAsync; PasswordHash вручну не задається.
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = userName, Email = email, DisplayName = displayName
        };
        var result = await users.CreateAsync(user, input.Password!);

        // 5. Очікувані IdentityResult.Errors → обережний 409 (duplicate) або 400 (інше).
        if (!result.Succeeded)
        {
            var codes = result.Errors.Select(error => error.Code).ToHashSet(StringComparer.Ordinal);
            if (codes.Contains("DuplicateUserName") || codes.Contains("DuplicateEmail"))
            {
                LogAuthEvent(logger, "register", "rejected_duplicate", null, httpContext);
                // Одне формулювання для зайнятого імені та зайнятого email.
                return Results.Problem(
                    title: "Обліковий запис не створено",
                    detail: "Ім’я користувача або email уже використовується.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var identityErrors = new Dictionary<string, string[]>();
            if (codes.Any(code => code.StartsWith("Password", StringComparison.Ordinal)))
                identityErrors["password"] = [PasswordPolicyMessage];
            if (codes.Contains("InvalidUserName"))
                identityErrors["userName"] = ["Недопустимі символи в імені користувача."];
            if (codes.Contains("InvalidEmail"))
                identityErrors["email"] = ["Некоректний email."];
            if (identityErrors.Count == 0)
                identityErrors["registration"] = ["Реєстрацію не виконано."];

            LogAuthEvent(logger, "register", "rejected_policy", null, httpContext);
            return Results.ValidationProblem(identityErrors);
        }

        var role = await users.AddToRoleAsync(user, DbSeeder.ReporterRole);
        if (!role.Succeeded) throw new InvalidOperationException("Default role assignment failed.");

        LogAuthEvent(logger, "register", "success", user.Id, httpContext);

        // 6. Лише затверджений response DTO: без password, hash, stamp, email, role.
        return Results.Created("/api/me", new RegistrationResponse(user.Id, user.UserName!, user.DisplayName));
    }

    private static Dictionary<string, string[]> ValidateRegistration(
        RegistrationInput input, out string userName, out string email, out string displayName)
    {
        var errors = new Dictionary<string, string[]>();

        userName = input.UserName?.Trim() ?? string.Empty;
        email = input.Email?.Trim() ?? string.Empty;
        displayName = input.DisplayName?.Trim() ?? string.Empty;

        if (userName.Length == 0)
            errors["userName"] = ["Поле userName обов’язкове."];
        else if (userName.Length < UserNameMinLength || userName.Length > UserNameMaxLength)
            errors["userName"] = [$"Довжина userName: від {UserNameMinLength} до {UserNameMaxLength} символів."];
        else if (!UserNamePattern.IsMatch(userName))
            errors["userName"] = ["userName може містити лише латинські літери, цифри та символи . _ -"];

        if (email.Length == 0)
            errors["email"] = ["Поле email обов’язкове."];
        else if (email.Length > EmailMaxLength)
            errors["email"] = [$"Довжина email: не більше {EmailMaxLength} символів."];
        else if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            errors["email"] = ["Некоректний email."];

        if (displayName.Length == 0)
            errors["displayName"] = ["Поле displayName обов’язкове."];
        else if (displayName.Length > DisplayNameMaxLength)
            errors["displayName"] = [$"Довжина displayName: не більше {DisplayNameMaxLength} символів."];

        if (string.IsNullOrEmpty(input.Password))
            errors["password"] = ["Поле password обов’язкове."];
        else if (input.Password.Length > PasswordMaxLength)
            errors["password"] = [$"Довжина password: не більше {PasswordMaxLength} символів."];

        return errors;
    }

    // Подія автентифікації: тип, результат, Guid користувача (якщо відомий), trace id.
    // НЕ пишемо: password, введене ім'я, email, cookie, hash, token, Authorization header, request body.
    private static void LogAuthEvent(ILogger logger, string eventType, string result, Guid? userId, HttpContext httpContext)
    {
        logger.LogInformation(
            "Auth event {EventType}: result {Result}, user {UserId}, trace {TraceId}",
            eventType, result, userId, httpContext.TraceIdentifier);
    }
}

// Request DTO: лише поля, які дозволено надсилати клієнту. Role, Id, PasswordHash тут немає.
public sealed record RegistrationInput(string? UserName, string? Email, string? DisplayName, string? Password);
public sealed record LoginInput(string? UserName, string? Password);

// Response DTO: безпечні поля відповіді.
public sealed record RegistrationResponse(Guid Id, string UserName, string DisplayName);
public sealed record CurrentUserResponse(Guid UserId, string UserName, string Role);
