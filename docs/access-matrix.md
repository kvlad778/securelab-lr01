# Матриця доступу ЛР04 — Incident, baseline 2-A

Рівень виконання: **добрий («Добре»)**; достатній рівень включено. Рядки відмінного рівня позначено.
Роль і власника сервер бере лише з verified principal (`HttpContext.User`): `ClaimTypes.NameIdentifier`
(Guid користувача) і server-issued role claim (`Reporter`, `Analyst`, `Administrator`), сформовані під час login.
Значення role/owner/status з body, query, URL чи заголовків не є входом авторизації.

## Політика відмов (403/404)

- **401 Unauthorized** — немає authentication context (анонім). Уся група `/api/incidents` і
  `/api/incident-comments/{id}` захищені `RequireAuthorization()`; cookie-профіль ЛР 3 повертає 401, а не redirect.
- **404 Not Found для чужого головного ресурсу** — API не розрізняє «не існує» і «недоступний цьому principal».
  Однаковий helper `IncidentNotFound()` (title «Інцидент не знайдено», без detail з id) для details, update,
  delete, triage; `CommentNotFound()` для child. Обґрунтування: існування інциденту іншого Reporter (назва,
  severity, факт звернення) — конфіденційна інформація; Alice не повинна навіть дізнатися, що id Bob існує.
- **403 Forbidden** — лише коли principal **уже має право бачити** ресурс, але не має права на конкретну дію
  (Analyst → update/delete чужого; Reporter-власник → triage; власник → delete не-New). Тут існування ресурсу
  не секрет для цього суб'єкта, а явна відмова підказує клієнтові, що дія заборонена.
- **409 Conflict** — дія дозволена роллю, але контекст стану не дозволяє (triage не-New інциденту).

## Матриця

| Дія | Рівень | Анонім | Reporter | Analyst | Administrator | Власник ресурсу має значення? | Стан реалізації |
|---|---|---:|---:|---:|---:|---:|---|
| Перегляд списку `GET /api/incidents` | добрий | deny, 401 | лише власні (query scoping за OwnerUserId) | усі інциденти (для аналізу) | лише власні (адмін-прав на інциденти не опубліковано) | Так для Reporter/Administrator | `IncidentQueries.GetListAsync` (userId, canSeeAll); тест A05_ListAsAlice_ContainsOnlyOwnIncidents |
| Пошук `GET /api/incidents/search` | відмінний (A-11) | не виконувалось — рівень відмінний (зараз маршрут відкритий, залишковий ризик) | — | — | — | — | не виконувалось — рівень відмінний |
| Зведення `GET /api/incidents/severity-summary` | відмінний (A-11) | deny, 401 (захищено разом із групою) | не обмежено за власником — рівень відмінний | — | — | — | не виконувалось — рівень відмінний (лише 401 для аноніма) |
| Перегляд деталей `GET /api/incidents/{id}` | достатній, entry-контракт ЛР 5; **головна action** | deny, 401 | власний — allow 200; чужий — deny, 404 як для неіснуючого | allow 200 для аналізу (будь-який інцидент), бачить internal comment | власний — allow; чужий — deny, 404 | Так | `IncidentQueries.GetDetailsAsync` (id && (isAnalyst ‖ owner)); тести A02, A03, A03R, A10 |
| Створення `POST /api/incidents` | успадкований baseline | deny, 401 | allow for self (owner = principal) | allow for self | allow for self | Сервер призначає власника | `Lab02Endpoints.CreateIncidentAsync` з ЛР 3; тести ЛР 3 A-01, A-04, A-05 |
| Update content `PUT /api/incidents/{id}` | добрий (повний perimeter) | deny, 401 | власний — allow 204 (лише title/description); чужий — deny, 404 | deny, 403 (read-дозвіл не дає права змінювати content) | власний — allow; чужий — deny, 404 | Так | handler `IncidentOperations.Update` (isOwner); тести A04_NonOwnerUpdate, A03R_AnalystUpdate, Update_AsOwner, A08a |
| Delete `DELETE /api/incidents/{id}` | добрий | deny, 401 | власний у статусі New — allow 204; власний не-New — deny, 403; чужий — deny, 404 | deny, 403 | власний New — allow; чужий — deny, 404 | Так | handler `IncidentOperations.Delete` (isOwner && New); тести A04_NonOwnerDelete, Delete_AsOwnerOfNewIncident |
| Зміна status: triage `POST /api/incidents/{id}/triage` | добрий | deny, 401 | deny: власний — 403 + audit `authorization.denied`; чужий — 404 | allow лише New → Triaged (200, next status визначає сервер); не-New — 409 | deny: як Reporter (власний — 403, чужий — 404) | Ні для дозволу (вирішує роль Analyst); видимість ресурсу перевіряється окремо | handler `IncidentOperations.Triage` + перевірка status у endpoint; тести A06, A07; audit A-09 |
| Зміна severity | добрий | deny, 401 | deny: поля severity немає в UpdateIncidentRequest (ігнорується) | окремого маршруту немає — не застосовується | не застосовується | — | A08a: severity лишається Low |
| Перегляд internal comment (`GET /api/incident-comments/{id}` і details-проєкція) | добрий | deny, 401 | deny: internal не повертається ні в details, ні за прямим id (404) | allow після авторизації parent incident | deny (як Reporter) | Ні для internal rule; доступ до parent incident перевіряється в тому самому запиті | `IncidentQueries.GetCommentAsync`, `GetDetailsAsync`; тести A12_* |
| Child: звичайний коментар `GET /api/incident-comments/{id}` | добрий | deny, 401 | коментар до власного інциденту — allow 200; до чужого — deny, 404 | allow 200 | як Reporter | Так (через parent incident) | `IncidentQueries.GetCommentAsync`; тести A12_ReporterReadsForeignCommentDirectly, A12_OwnerReadsPublicComment |
| Export/audit | відмінний, якщо flow існує | — | — | — | — | — | не застосовується (flow відсутній) |

Child resource, через який можна непрямо отримати дані головного ресурсу: **IncidentComment**
(`GET /api/incident-comments/{id}`), а також колекція `comments` у details-проєкції.
Administrator не має автоматичного обходу ownership: у baseline 2-A йому не опубліковано додаткових
прав щодо Incident, тому за принципом least privilege — deny.
