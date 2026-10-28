using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using SecureLab.Api.Scaffolding;
using Xunit.Abstractions;

namespace SecureLab.Api.Tests;

// Захист ЛР 02: точний порядок пошуку без sortBy.
// Контракт: CreatedAtUtc за спаданням, далі Id за зростанням (tie-breaker).
// Перед запуском БД скидається до seed (--reset-database), тому новіших записів, ніж 0004/0005, немає.
public sealed class Lab02DefenseOrderTests(SecureLabApiFactory factory, ITestOutputHelper output)
    : IClassFixture<SecureLabApiFactory>
{
    private static readonly Guid Seed0004 = Guid.Parse("20000000-0000-0000-0000-000000000004");
    private static readonly Guid Seed0005 = Guid.Parse("20000000-0000-0000-0000-000000000005");

    // Seed-тест пошуку без sortBy (початкова версія — доповнюється на захисті).
    [Fact]
    public async Task Search_WithoutSortBy_ReturnsSeedRows0004And0005()
    {
        var sql = new SqlCapture();
        await using var app = factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(sql)));
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/incidents/search");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<IncidentSearchItemResponse>>();
        Assert.NotNull(items);

        // ПОЧАТКОВЕ assertion: перевіряє лише НАЯВНІСТЬ двох Id (множину), порядок не перевіряється.
        // Assert.Contains(items, item => item.Id == Seed0004);
        // Assert.Contains(items, item => item.Id == Seed0005);
        output.WriteLine($"Отримано записів: {items.Count}");
        Assert.Equal(Seed0004, items[0].Id);
        Assert.Equal(Seed0005, items[1].Id);
        Assert.Equal(items[0].CreatedAtUtc, items[1].CreatedAtUtc);
        var searchSql = sql.Commands.Last(text => text.Contains("ILIKE", StringComparison.Ordinal));
        output.WriteLine(searchSql);
        Assert.Matches(@"ORDER BY \w+\.created_at_utc DESC, \w+\.id\s+LIMIT", searchSql);
    }

    // Власний контрприклад із трьох рядків. Набір статичний і в БД НЕ вставляється.
    // A і B мають однаковий новіший час, C старіший, але його Id найменший.
    [Fact]
    public void Counterexample_ThenByKeepsContract_SecondOrderByBreaksIt()
    {
        var newer = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var older = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var a = new Row("A", Guid.Parse("90000000-0000-0000-0000-000000000002"), newer);
        var b = new Row("B", Guid.Parse("90000000-0000-0000-0000-000000000003"), newer);
        var c = new Row("C", Guid.Parse("90000000-0000-0000-0000-000000000001"), older);
        Row[] rows = [b, c, a]; // довільний вхідний порядок

        var byContract = rows
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenBy(row => row.Id)
            .Select(row => row.Name);
        var withSecondOrderBy = rows
            .OrderByDescending(row => row.CreatedAtUtc)
            .OrderBy(row => row.Id) // помилка: другий OrderBy скидає перший ключ
            .Select(row => row.Name);

        Assert.Equal("A, B, C", string.Join(", ", byContract));
        Assert.Equal("C, A, B", string.Join(", ", withSecondOrderBy));
    }

    private sealed record Row(string Name, Guid Id, DateTimeOffset CreatedAtUtc);

    // Збирає тексти SQL-команд EF Core під час цього тесту (параметри не розкриваються).
    private sealed class SqlCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public ILogger CreateLogger(string categoryName) =>
            new SqlLogger(categoryName == "Microsoft.EntityFrameworkCore.Database.Command" ? Commands : null);

        public void Dispose()
        {
        }

        private sealed class SqlLogger(ConcurrentQueue<string>? sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => sink is not null;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                sink?.Enqueue(formatter(state, exception));
            }
        }
    }
}
