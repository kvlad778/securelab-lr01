const listElement = document.querySelector("#incident-list");
const listStatusElement = document.querySelector("#list-status");
const detailsElement = document.querySelector("#incident-details");
const filterForm = document.querySelector("#filter-form");
const summaryForm = document.querySelector("#summary-form");
const summaryButton = document.querySelector("#summary-button");
const summaryMessage = document.querySelector("#summary-message");
const summaryList = document.querySelector("#summary-list");

async function apiFetch(path, options = {}) {
  const response = await fetch(path, {
    headers: { Accept: "application/json", ...options.headers },
    ...options,
  });

  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.title ?? `HTTP ${response.status}`);
  }

  return response.json();
}

function createTextElement(tagName, text, className) {
  const element = document.createElement(tagName);
  element.textContent = text;
  if (className) {
    element.className = className;
  }
  return element;
}

function renderIncidentList(incidents) {
  listElement.replaceChildren();

  if (incidents.length === 0) {
    listStatusElement.textContent = "За заданим фільтром інцидентів немає.";
    return;
  }

  listStatusElement.textContent = `Знайдено: ${incidents.length}`;
  for (const incident of incidents) {
    const item = document.createElement("li");
    const button = document.createElement("button");
    button.type = "button";
    button.className = "incident-card";
    button.append(
      createTextElement("strong", incident.title),
      createTextElement("span", `${incident.severity} · ${incident.status}`, "metadata"),
    );
    button.addEventListener("click", () => loadIncidentDetails(incident.id));
    item.append(button);
    listElement.append(item);
  }
}

function renderIncidentDetails(incident) {
  const heading = createTextElement("h3", incident.title);
  const metadata = createTextElement(
    "p",
    `${incident.severity} · ${incident.status} · автор: ${incident.ownerDisplayName}`,
    "metadata",
  );
  const description = createTextElement("p", incident.description);
  const commentsHeading = createTextElement("h4", "Коментарі");
  const comments = document.createElement("ul");

  for (const comment of incident.comments) {
    const item = document.createElement("li");
    item.append(
      createTextElement("strong", `${comment.authorDisplayName}: `),
      document.createTextNode(comment.text),
    );
    comments.append(item);
  }

  if (incident.comments.length === 0) {
    comments.append(createTextElement("li", "Коментарів немає."));
  }

  detailsElement.className = "";
  detailsElement.replaceChildren(heading, metadata, description, commentsHeading, comments);
}

async function loadIncidents() {
  listStatusElement.textContent = "Завантаження…";
  listElement.replaceChildren();

  const status = new FormData(filterForm).get("status");
  const query = status ? `?status=${encodeURIComponent(status)}` : "";

  try {
    renderIncidentList(await apiFetch(`/api/incidents${query}`));
  } catch (error) {
    listStatusElement.textContent = `Помилка: ${error.message}`;
  }
}

async function loadIncidentDetails(id) {
  detailsElement.className = "details-placeholder";
  detailsElement.textContent = "Завантаження…";

  try {
    renderIncidentDetails(await apiFetch(`/api/incidents/${encodeURIComponent(id)}`));
  } catch (error) {
    detailsElement.textContent = `Помилка: ${error.message}`;
  }
}

async function loadSeveritySummary() {
  summaryButton.disabled = true;
  summaryMessage.textContent = "Завантаження…";
  summaryList.replaceChildren();

  const status = new FormData(summaryForm).get("status");
  const query = status ? `?status=${encodeURIComponent(status)}` : "";

  try {
    const summary = await apiFetch(`/api/incidents/severity-summary${query}`);

    // Порожній стан: сервер повернув [] (наприклад, для статусу Resolved)
    if (summary.length === 0) {
      summaryMessage.textContent = "Даних немає";
      return;
    }

    for (const entry of summary) {
      const item = document.createElement("li");
      item.textContent = `${entry.severity}: ${entry.count}`;
      summaryList.append(item);
    }
    summaryMessage.textContent = `Груп: ${summary.length}`;
  } catch {
    // Фіксоване повідомлення без технічних деталей
    summaryMessage.textContent = "Не вдалося завантажити підсумок. Спробуйте пізніше.";
  } finally {
    summaryButton.disabled = false;
  }
}

filterForm.addEventListener("submit", (event) => {
  event.preventDefault();
  loadIncidents();
});

summaryForm.addEventListener("submit", (event) => {
  event.preventDefault();
  loadSeveritySummary();
});

loadIncidents();
