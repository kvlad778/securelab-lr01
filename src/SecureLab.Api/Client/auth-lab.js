// ЛР 03, добрий рівень: браузерний сценарій register → login → profile → create → logout.
// Пароль і principal не зберігаються в localStorage/sessionStorage, DOM dataset чи Console.
// Дані показуються лише через textContent (без innerHTML).
const result = document.querySelector("#result");
const stateBox = document.querySelector("#state");

// fetch лише на свій origin; браузер сам додає HttpOnly cookie (credentials: same-origin).
function apiFetch(path, options = {}) {
  const url = new URL(path, location.origin);
  if (url.origin !== location.origin) {
    throw new Error("Потрібна адреса цього застосунку.");
  }
  return fetch(url, { ...options, credentials: "same-origin" });
}

function postJson(path, data) {
  return apiFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(data)
  });
}

function showResult(text) {
  result.textContent = text;
}

function showState(state) {
  const texts = {
    "anonymous": "Не ввійшли (анонімний стан).",
    "signed-in": "Сеанс активний.",
    "unknown": "Стан сеансу невизначений (помилка сервера або мережі)."
  };
  stateBox.textContent = texts[state];
  stateBox.className = `state state-${state}`;
}

function clearProfile() {
  document.querySelector("#profile").hidden = true;
  document.querySelector("#profile-empty").hidden = false;
  for (const id of ["profile-id", "profile-name", "profile-role"]) {
    document.querySelector(`#${id}`).textContent = "";
  }
}

function showProfile(profile) {
  document.querySelector("#profile-id").textContent = String(profile.userId ?? "");
  document.querySelector("#profile-name").textContent = String(profile.userName ?? "");
  document.querySelector("#profile-role").textContent = String(profile.role ?? "");
  document.querySelector("#profile").hidden = false;
  document.querySelector("#profile-empty").hidden = true;
}

// Безпечний опис помилки: лише status і назви полів з errors, без тіла запиту.
async function describeError(response) {
  let keys = "";
  try {
    if ((response.headers.get("Content-Type") ?? "").includes("json")) {
      const problem = await response.json();
      if (problem && problem.errors) {
        keys = ` (поля: ${Object.keys(problem.errors).join(", ")})`;
      } else if (problem && problem.title) {
        keys = ` (${problem.title})`;
      }
    }
  } catch {
    keys = "";
  }
  return `${response.status}${keys}`;
}

async function loadProfile() {
  let response;
  try {
    response = await apiFetch("/api/me");
  } catch {
    clearProfile();
    showState("unknown");
    showResult("Профіль: помилка мережі.");
    return;
  }
  if (response.status === 401) {
    clearProfile();
    showState("anonymous");
    return;
  }
  const contentType = response.headers.get("Content-Type") ?? "";
  if (!response.ok || !contentType.includes("application/json")) {
    clearProfile();
    showState("unknown");
    showResult(`Профіль: помилка ${response.status}`);
    return;
  }
  showProfile(await response.json());
  showState("signed-in");
}

document.querySelector("#register").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = Object.fromEntries(new FormData(form));
  form.elements.password.value = "";
  try {
    const response = await postJson("/api/auth/register", data);
    if (response.status === 201) {
      form.reset();
      showResult("Реєстрація: 201. Обліковий запис створено — тепер увійдіть.");
    } else if (response.status === 409) {
      showResult("Реєстрація: 409. Ім’я користувача або email уже використовується.");
    } else {
      showResult(`Реєстрація: ${await describeError(response)}`);
    }
  } catch {
    showResult("Помилка мережі під час реєстрації.");
  } finally {
    delete data.password;
  }
});

document.querySelector("#login").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = Object.fromEntries(new FormData(form));
  form.elements.password.value = "";
  try {
    const response = await postJson("/api/auth/login", data);
    if (response.status === 204) {
      showResult("Login: 204");
    } else if (response.status === 429) {
      showResult("Login: 429. Забагато спроб входу, спробуйте пізніше.");
    } else {
      showResult(`Login: ${response.status}. Невірне ім’я користувача або пароль.`);
    }
  } catch {
    showResult("Помилка мережі під час входу.");
  } finally {
    delete data.password;
    await loadProfile();
  }
});

document.querySelector("#incident").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const values = Object.fromEntries(new FormData(form));
  // Власний request DTO: OwnerUserId, Role, Status, Id не надсилаються — їх визначає сервер.
  const request = {
    title: values.title,
    description: values.description,
    severity: values.severity,
    occurredAtUtc: new Date(values.occurredAt).toISOString()
  };
  try {
    const response = await postJson("/api/incidents", request);
    if (response.status === 201) {
      const created = await response.json();
      form.reset();
      showResult(`Інцидент: 201. id ${created.id}, «${created.title}», status ${created.status}`);
    } else if (response.status === 401) {
      clearProfile();
      showState("anonymous");
      showResult("Інцидент: 401. Спершу увійдіть.");
    } else {
      showResult(`Інцидент: ${await describeError(response)}`);
    }
  } catch {
    showResult("Помилка мережі під час створення інциденту.");
  }
});

document.querySelector("#me").addEventListener("click", async () => {
  await loadProfile();
});

document.querySelector("#logout").addEventListener("click", async () => {
  try {
    const response = await apiFetch("/api/auth/logout", { method: "POST" });
    clearProfile();
    showResult(`Logout: ${response.status}`);
  } catch {
    showResult("Помилка мережі під час виходу.");
  } finally {
    // Повторний GET /api/me тим самим браузером: після sign-out очікуємо 401 (анонімний стан).
    await loadProfile();
  }
});

loadProfile();
