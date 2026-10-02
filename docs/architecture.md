# Карта архітектури

## Компоненти

| Компонент | Розташування | Відповідальність |
|---|---|---|
| Browser client | `src/SecureLab.Api/Client/` | Надсилає HTTP-запити, безпечно показує відповідь через DOM API |
| Presentation | `Presentation/` | Описує endpoints, перевіряє параметри, формує HTTP-відповідь |
| Application | `Application/` | Виконує запити: список, деталі, підсумок за severity |
| Data (EF Core) | `Data/` | Відображає C#-сутності на таблиці PostgreSQL |
| PostgreSQL | `infra/compose.yaml` | Зберігає навчальні дані у локальному контейнері Docker |

## Вибраний маршрут: підсумок за severity

`GET /api/incidents/severity-summary` (необов'язковий параметр `status`).

```text
кнопка «Показати підсумок» у Client/index.html
  → submit-обробник і loadSeveritySummary у Client/app.js
  → GET /api/incidents/severity-summary[?status=...]
  → IncidentEndpoints.GetSeveritySummaryAsync (перевірка status, інакше 400)
  → IncidentQueries.GetSeveritySummaryAsync
  → SecureLabDbContext.Incidents / таблиця incidents
  → IncidentSeveritySummaryResponse як JSON
  → li.textContent у списку підсумку
```

Ключові файли:

| Рівень | Файл |
|---|---|
| Клієнт | `Client/index.html`, `Client/app.js` |
| Endpoint | `Presentation/Endpoints/IncidentEndpoints.cs` |
| Application | `Application/Incidents/IncidentQueries.cs` |
| DTO | `Presentation/Contracts/IncidentResponses.cs` |
| DbContext і таблиця | `Data/SecureLabDbContext.cs` → `incidents` |

Контракт: політика нульових груп: лише наявні групи (порожній результат `[]`).
Порядок: за кількістю за спаданням, при рівній кількості за назвою від A до Z.
Сортування виконується в пам'яті після завантаження агрегату, бо severity у БД
зберігається як текст. На початкових даних: High 1, Low 1, Medium 1; Critical відсутній.

## Межі довіри

| Межа | Чому даним ще не можна довіряти | Де перевіряємо або обмежуємо |
|---|---|---|
| Користувач → Browser client | Користувач контролює введення; `<select>` обмежує лише цей інтерфейс | Клієнтські обмеження не вважаються контролем |
| Browser client → API | method, URL і параметр `status` можна змінити поза UI | `:guid` для id; перевірка `status` за списком дозволених назв (400); 404 для відсутнього запису |
| API → PostgreSQL | Параметри запиту походять від клієнта | Параметризація EF Core, `AsNoTracking()`, явна проєкція |
| API → браузер | Право читати entity не означає право бачити всі поля | Окремі response DTO без `OwnerUserId`, email, внутрішніх коментарів |
| PostgreSQL → API → DOM | У БД може бути раніше введений недовірений текст (seed містить `<script>`) | `textContent` і `createElement`, без небезпечних HTML-приймачів |

## Конфігураційні входи

- `global.json` — вибір версії .NET SDK;
- `src/SecureLab.Api/appsettings*.json` — режим міграцій і локальний connection string;
- `infra/compose.yaml` — версія PostgreSQL, порт і локальні навчальні облікові дані;
- змінна середовища `ConnectionStrings__SecureLab` — перевизначає connection string поза репозиторієм.
Опублікований порт у Compose має збігатися з портом у connection string, інакше API не підключиться до БД.

## Повернення до відомого стану

Зупинити API (Ctrl+C), потім
`dotnet run --project src/SecureLab.Api -- --reset-database`.
Команда застосовує migration, очищує лише навчальні таблиці й повторно заповнює seed.
Працює лише в Development і лише для локальної навчальної БД.