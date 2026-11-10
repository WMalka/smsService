# SMS Sending Service

A service that sends an SMS to one or more employee phone numbers through an external SMS provider, stores every message, and reports its status.

```bash
docker compose up --build
```

UI: http://localhost:8080 · API + Swagger: http://localhost:5080/swagger

The full design — every decision with its alternatives, advantages and disadvantages — is in **[SPEC.md](SPEC.md)**. This README is the short version. The Part B extension document (second provider, load, failures, monitoring) is **[PART-B.md](PART-B.md)** (Hebrew).

---

## 1. What I built

**Required by the task**

- `POST /messages` — takes `to` and `body`, returns the message id.
- `GET /messages/{id}` — returns the message status.
- `GET /messages?status=` — returns a list filtered by status.
- Input validation: phone number format, message length.
- Persistence in a database (SQLite).
- Automated tests for the critical logic.
- A simple UI: send form + messages table with statuses.
- Runs locally with one command.

**Beyond the minimum, and why**

| Feature | Why |
|---|---|
| Several recipients per message, one status per recipient | Messages go to employees, who can have several numbers. A message is `PartiallySent` when only some recipients failed. |
| Sending in the background with retries | The API stays fast and keeps working when the provider is slow or down. A crash loses nothing. |
| `Delivered` status | "The provider accepted it" is not "the phone got it" (the task's Part B question). |
| Idempotency key on `POST` | A double click does not send twice. |
| Cursor (keyset) paging | The list stays correct while new messages keep arriving. |
| Swagger | Try and debug the API from the browser. |

**Stack:** C# / ASP.NET Core (.NET 8 LTS), EF Core + SQLite, xUnit · React 18 + TypeScript + Vite, TanStack Query, Vitest · Docker Compose + nginx.

**Structure**

```
src/Sms.Core/            Business rules and use cases. No dependencies on databases, HTTP or packages.
src/Sms.Infrastructure/  Database (EF Core), the SMS provider adapter, the background sender.
src/Sms.Api/             MessagesController, Swagger, error handling, wiring.
tests/                   Sms.Core.Tests (rules, use cases) · Sms.Api.Tests (HTTP, database, provider adapter).
web/                     React UI.
seed/employees.json      Sample employees loaded at startup.
```

**Tests:** 120 automated tests (92 Core, 25 API/integration, 3 UI). Section 7.1 of the SPEC maps every piece of critical logic to its tests.

---

## 2. How to run

### With Docker (one command)

```bash
docker compose up --build
```

| URL | What |
|---|---|
| http://localhost:8080 | UI |
| http://localhost:5080/swagger | API documentation, try every endpoint |
| http://localhost:5080/health | Health check (API + database) |

Data is kept in the `sms-data` Docker volume. `docker compose down -v` deletes it.

### The SMS provider

The provider is the mock server **supplied with the task**; it is not part of this repository. The API expects it on the host machine at port 5005. To use a different address:

```bash
SMS_PROVIDER_URL=http://host.docker.internal:6000 docker compose up --build
```

```powershell
$env:SMS_PROVIDER_URL='http://host.docker.internal:6000'; docker compose up --build
```

Paths and timeout are in `src/Sms.Api/appsettings.json`. They are configuration, not code, because they change between the supplied mock and a real provider:

```json
"SmsProvider": { "BaseUrl": "http://localhost:5005", "SendPath": "/send",
                 "StatusPath": "/status/{providerId}", "TimeoutSeconds": 10 }
```

Without a provider, messages are accepted but each recipient ends as `Failed` after 3 attempts.

### Without Docker (Windows, .NET 8 SDK + Node 20)

```powershell
./run.ps1
```

API on http://localhost:5080, UI on http://localhost:5173. Ctrl+C stops both.

### Tests

```bash
dotnet test
```

```bash
cd web && npm test
```

### Debugging

- **Visual Studio / Rider:** open `Sms.sln`, start `Sms.Api` with the `http` profile (F5), set breakpoints in `MessagesController`, `SendMessage` or `DispatchPending`.
- **Requests:** [src/Sms.Api/Sms.Api.http](src/Sms.Api/Sms.Api.http) has ready requests (Visual Studio, or VS Code with REST Client), or use Swagger.
- **UI:** `cd web && npm run dev`, then the browser DevTools (Network tab for API calls).
- **Database:** `src/Sms.Api/sms.db` opens in any SQLite viewer.

---

## 3. API

| Method | Path | Result |
|---|---|---|
| `POST` | `/messages` | `202 Accepted` + `Location` and the message. Body: `{"to": ["050-1234567"], "body": "text"}`. Optional header `Idempotency-Key`. |
| `GET` | `/messages/{id}` | `200` with every recipient's status, or `404`. |
| `GET` | `/messages?status=&cursor=&limit=` | `200` with `{items, nextCursor}`, newest first. Pass `nextCursor` as `cursor` for the next page. |
| `GET` | `/health` | `200` Healthy / `503` Unhealthy. |

Statuses — recipient: `Pending`, `Sent`, `Delivered`, `Failed`. Message: the same plus `PartiallySent`.

Invalid input returns `400` with one error per field, e.g. `{"errors": {"to[1]": ["Phone number must be an Israeli mobile number."]}}`.

```bash
curl -i -X POST http://localhost:5080/messages \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: 3f2a9c1e-7b1d-4c55-9a0e-2d1f6c8b9e10" \
  -d '{"to": ["050-1234567", "+972521112233"], "body": "Shift starts at 07:00"}'
```

---

## 4. Assumptions

The task asks to define assumptions instead of asking questions.

| # | Assumption |
|---|---|
| A1 | Recipients are Israeli mobile numbers: `05XXXXXXXX`, `05X-XXXXXXX` or `+9725XXXXXXXX`. Stored as `+9725XXXXXXXX`. |
| A2 | Message text: 1–500 characters after trimming. Hebrew SMS parts are 70 characters, so 500 allows a long message without abuse. |
| A3 | Provider contract (not given, so assumed): `POST /send {to, body, reference}` → `{providerId}`; `GET /status/{providerId}` → `{status: pending\|delivered\|failed}`; a repeated `reference` is not sent twice; 4xx = permanent error, 5xx/timeout = temporary error. |
| A4 | An employee is identified by first name + last name + address. One employee can have many phone numbers; a number may be shared by employees. |
| A5 | `POST /messages` takes phone numbers, as the task defines. Employees are loaded from `seed/employees.json` and modeled, but the endpoints do not use them. |
| A6 | One organization, one running copy of the API. |
| A7 | Sending is at-least-once on our side. It is exactly once only if the provider ignores a repeated `reference` — not verified, so not promised. |
| A8 | 1–100 recipients per request; duplicate numbers are sent once; one invalid number rejects the whole request. |

---

## 5. Decisions and why

| Decision | Why | Main alternative |
|---|---|---|
| **Save first, send in the background** (SPEC D4) | Fast API, independent of the provider; nothing lost on a crash; retries in one place. | Send inside the request: provider down = API down. |
| **The database table is the queue; no RabbitMQ/Kafka** (D4) | Nothing extra to run. A broker would not remove the table: saving and publishing are two writes that can split on a crash. | RabbitMQ — the right next step when several workers are needed. |
| **SQL (SQLite) + EF Core** (D3) | The data is relational with uniqueness rules and transactions. SQLite needs no installation. EF Core makes PostgreSQL / SQL Server a configuration change. | MongoDB: weaker uniqueness rules and joins. |
| **`202 Accepted`** (D18) | The message is saved but not sent yet; `Location` points to its status. | `201 Created` suggests the work is done. |
| **No duplicate SMS: idempotency key + version check + provider reference** (D16) | Each closes a different hole: double click, double processing, re-send after a crash. | Accept rare duplicates. |
| **Statuses per recipient; message status calculated from them** (D13) | Shows a mixed result honestly (`PartiallySent`). "Sending…" is a UI label, not a stored status. | One status per message: hides partial failures. |
| **`Delivered` by asking the provider; checks thin out over time** (D13) | Half the time since sending (10 s – 1 h): ~30 calls per recipient over 24 h instead of ~8,600. | Webhooks: need provider support and a public URL. |
| **Parallel sending with a limit** (D17) | Many numbers at once without flooding the provider. | No limit: rate-limit errors and timeouts. |
| **Cursor (keyset) paging** (D8) | Stable while new messages arrive; fast at any depth. | Page numbers: rows shift and repeat. |
| **Controllers + Swagger, no MediatR** (D1) | Familiar and self-documenting; Swagger to try the API; no extra indirection. | Minimal API: less code, read worse here. |
| **3 projects: Core / Infrastructure / Api** (D2) | Business rules cannot depend on the database or HTTP, without extra projects. | 4 projects (separate Domain + Application). |
| **Validation in `PhoneNumber` and `MessageBody`** (D5) | Rules in one place; an invalid message cannot exist. | Validation attributes on request classes. |
| **Employee tables kept, not used by the endpoints** (D19, D20) | The task describes the employee rules explicitly; a phone number does not identify one employee, so messages address numbers. | No employee tables. |
| **SQL injection: parameterized queries, not text filtering** (SPEC §9) | Text like `"OR 1=1"` is a valid message. Filtering breaks real messages and is easy to bypass. | Block "dangerous" words. |

---

## 6. What I chose not to do, and why

| Not done | Why |
|---|---|
| Authentication, deployment | The task says they are not needed. |
| Message broker (RabbitMQ / Kafka) | One process and ≤100 recipients per request do not need it (D4). |
| Delivery webhooks | Need provider support; the supplied mock offers GET only. |
| A second provider / failover | One adapter behind `ISmsProvider`; a second one is a new class plus one line of wiring. |
| Campaigns of hundreds of thousands | Part B. SQLite and one sender are the limits. |
| EF Core migrations | `EnsureCreated` is used as a marked shortcut. It cannot change an existing database, so migrations come first before real use. |
| Building our own provider | The task supplies one. Tests use an in-memory fake. |
| An endpoint to send by employee | Changes the API the task defines; the task gives no rule for "which of the employee's numbers". |

---

## 7. What I would add with more time

1. PostgreSQL (or the organization's SQL Server) with migrations; integration tests on the real database (Testcontainers).
2. Several API copies and senders: database row locking (`SKIP LOCKED`) or RabbitMQ fed by the table.
3. Circuit breaker on the provider, and failover to a second provider.
4. Delivery webhooks where the provider supports them.
5. A campaign endpoint with rate limiting for very large sends.
6. Metrics and alerts: failure rate, retries, age of the oldest waiting message.
7. Sending by employee, using the employee tables.

---

## 8. Known risks

- **The provider contract is assumed (A3).** Paths, JSON field names, `GET /status` and ignoring a repeated `reference` must be checked against the supplied mock. If they differ, only `MockSmsProvider` and `appsettings.json` change.
- **SQLite allows one writer at a time.** Fine for one API copy at this volume, not for many.

---

## 9. Time invested

About 3.5–4 hours in total, spread over several sessions rather than in one sitting.

---

## 10. AI usage

AI was used, as the task allows. The full SPEC the development followed is [SPEC.md](SPEC.md).

| Where | Why |
|---|---|
| Planning the SPEC, fixing it, weighing the decisions and looking for the best options | To compare more alternatives and trade-offs within the time limit. Every decision in the SPEC was reviewed and chosen by me. |
| Writing the code and tests from the SPEC (Claude Code) | To fit the implementation into the time limit and keep it consistent with the SPEC. |

I went through all the code and changed it to the way I want it. I can explain and modify any part of it.
