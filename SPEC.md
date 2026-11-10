# SPEC: SMS Sending Service

| | |
|---|---|
| **Version** | 1.5 |
| **Date** | 2026-11-10 |
| **Stack** | C# / ASP.NET Core (latest .NET LTS) + React (TypeScript) |
| **Time budget** | 3–4 hours, hard cap 4 hours. Anything not done goes to the README. |
| **Guiding principles** | TDD, SOLID, DRY, readable code that is easy to maintain, as little code as possible. **When short and readable conflict, readable wins.** |

---

## 1. Purpose and Scope

Build a service that sends an SMS message to one or more employees' phone numbers through an external SMS provider, stores every message and every recipient, and reports their status.

**In scope (Part A)**

- `POST /messages` (one body, many recipients), `GET /messages/{id}`, `GET /messages?status=`
- Input validation (phone numbers, message length)
- Persistence in a SQL database
- No duplicate SMS on double click; on a crash, no duplicate provided the provider honors `reference` (D16)
- Sending to many recipients in parallel, with a configurable limit (D17)
- Delivery confirmation: a recipient becomes `Delivered` when the provider confirms it (D13)
- Automated tests for the critical logic, written test-first
- A simple UI: send form + messages table with statuses (optional, not graded)
- Run locally with one command

**Out of scope**

- Authentication / authorization (explicitly not required)
- Deployment (explicitly not required)
- Building the SMS provider: it is **supplied by the task** (A3)
- A message broker (RabbitMQ / Kafka), delivery webhooks, multiple providers, campaigns of hundreds of thousands (Part B)
- Employee management API (A5)

---

## 2. Assumptions

The task asks to state assumptions instead of asking questions. Each one goes into the README as well.

| # | Assumption | Why |
|---|---|---|
| A1 | Recipients are Israeli mobile numbers. Accepted input: `05XXXXXXXX`, `05X-XXXXXXX` or E.164 `+9725XXXXXXXX`. Stored **normalized to E.164**. | One canonical form makes filtering, de-duplicating and sending reliable. |
| A2 | Message body: 1–500 characters after trimming. Control characters other than line breaks are rejected. Any other text, including quotes and SQL-like words, is valid (section 9). | Hebrew SMS segments are 70 characters (UCS-2). 500 allows a multi-part message without abuse. |
| A3 | The SMS provider is the mock server **supplied with the task**. We do not build it. Its base URL and paths live in `appsettings.json`. Assumed contract: `POST /send {to, body, reference}` returns `{providerId}`; a repeated `reference` does not send a second SMS. `GET /status/{providerId}` returns `{status: pending \| delivered \| failed}`. HTTP 4xx = permanent failure, 5xx or timeout = transient failure. | The exact contract was not given. It is hidden behind `ISmsProvider` and one adapter class (5.5), so the real contract changes one class. De-duplication on `reference` must be verified against the supplied mock. |
| A4 | An employee is identified by **First name + Last name + Address** together. There is no employee number. **One employee can have many phone numbers.** A phone number is **not** unique: two employees may share one. | Taken from the task text. Enforced by a unique index (D20). |
| A5 | `POST /messages` takes **phone numbers**, as the task defines the API. The employee tables are filled once at startup from `seed/employees.json` (when empty). No endpoint creates or edits employees, and the message endpoints do not use them in Part A. | Keeps the required contract while still modeling the employee rules. Sending by employee is an extension (D19). |
| A6 | One organization uses the system, and exactly one copy of the API process runs. | Local run only. The background sender needs no cross-process locking. Several copies are a Part B topic. |
| A7 | Our side guarantees **at-least-once** sending and processes each recipient's result once. The SMS reaches the phone **effectively once** only if the provider ignores a repeated `reference` (A3); this is not verified and is not promised. | True exactly-once delivery needs both sides (D16). |
| A8 | One request has 1–100 recipients. Duplicate numbers in one request (after normalization) are sent once. If any number is invalid, the whole request is rejected. | Large campaigns are Part B. All-or-nothing is predictable: the user fixes the list and resends. |

---

## 3. Functional Requirements

### FR-1 Send a message — `POST /messages`

Request:

```
POST /messages
Idempotency-Key: 3f2a9c1e-...        (optional, recommended)

{ "to": ["050-1234567", "+972521112233"], "body": "Shift starts at 07:00" }
```

Response `202 Accepted`, `Location: /messages/{id}`:

```json
{
  "id": "6f1c...",
  "body": "Shift starts at 07:00",
  "status": "Pending",
  "recipients": [
    { "id": "a91e...", "to": "+972501234567", "status": "Pending", "attempts": 0, "lastError": null },
    { "id": "b27d...", "to": "+972521112233", "status": "Pending", "attempts": 0, "lastError": null }
  ],
  "createdAt": "2026-11-10T08:00:00Z",
  "updatedAt": "2026-11-10T08:00:00Z"
}
```

- **Why 202 and not 201:** the request is saved but the SMS is not sent yet. `202 Accepted` says "received, processing continues in the background", and `Location` points to where the client checks the status (D18).
- The message is **saved before the response returns**. Sending happens in the background (D4), so the API stays fast and does not depend on provider uptime.
- **Idempotency:** a request with an `Idempotency-Key` that was already used returns the **same response** as the first time, and nothing new is created or sent. A unique index enforces it, so two identical requests at the same moment still create one message. The UI generates one key per form submission (`crypto.randomUUID()`).

### FR-2 Get one message — `GET /messages/{id}`

`200` with the message and its recipients (same shape as FR-1), or `404` ProblemDetails. A GET only reads, so it never returns `202`; the processing state is in the `status` fields.

### FR-3 List messages — `GET /messages?status=Sent&limit=20&cursor=...`

- `status` is optional: `Pending`, `Sent`, `Delivered`, `PartiallySent` or `Failed` (case-insensitive). Any other value returns `400`.
- **Keyset (cursor) paging** (D8): `limit` default 20, max 100. Newest first, ordered by `(CreatedAt, Id)`. The response has `nextCursor`; pass it as `cursor` to get the next page. `nextCursor` is `null` on the last page. An invalid cursor returns `400`.
- The cursor is opaque to the client (Base64 of the last item's `CreatedAt` and `Id`).
- Each item has counts instead of the full recipient list, to keep the list light:

```json
{
  "items": [
    { "id": "6f1c...", "body": "Shift starts...", "status": "PartiallySent",
      "recipientCount": 3, "deliveredCount": 1, "sentCount": 1, "failedCount": 1, "createdAt": "..." }
  ],
  "nextCursor": "MjAyNi0xMS0xMFQwODowMDowMFp8NmYxYy4uLg=="
}
```

### FR-4 Validation

- Invalid input returns `400` with RFC 7807 `ValidationProblemDetails`, one entry per field, with the index for a bad number:
  `{"errors": {"to[1]": ["Phone number must be an Israeli mobile number."]}}`
- The server is the only authority. The UI repeats the rules for user experience only.

### FR-5 Background sending

A background worker picks up due recipients, sends them **in parallel** (with a limit) through `ISmsProvider`, and updates their status (5.3, D17). The database table is the queue (D4). The same worker asks the provider about `Sent` recipients until they are `Delivered` or `Failed`, for up to 24 h.

### FR-6 UI (optional)

- Send form: recipients (one per line or comma-separated), body with a character counter, submit button disabled while submitting, one `Idempotency-Key` per submission.
- **After a successful send, the form clears** and a new idempotency key is prepared. After an error, the fields stay, so the user can fix them.
- Messages table: status filter, "Load more" button for the next page (keyset), refresh every few seconds while any message is `Pending` or `Sent`, plus a manual Refresh button.
- **Display labels:** the UI shows a `Pending` recipient as **"Sending…"** with a spinner. This is a label only; the stored status stays `Pending` (D13).
- In development the Vite dev server proxies `/messages` to the API, so no CORS code is needed.

### Statuses

Each **recipient** has its own status. The **message** status is calculated from its recipients.

| Recipient status | Meaning |
|---|---|
| `Pending` | Waiting to be sent or waiting for a retry. The UI shows it as "Sending…". |
| `Sent` | The provider accepted it; delivery to the phone not confirmed yet. |
| `Delivered` | The provider confirmed the phone received it. |
| `Failed` | The provider rejected it, all attempts failed, or the provider reported it was not delivered. `LastError` says why. |

```
Pending ──provider accepted──▶ Sent ──provider confirms──▶ Delivered
   │  ▲                          │
   │  └── transient error,       └── provider reports not delivered ──▶ Failed
   │      attempts < 3
   └───── permanent error, or 3rd attempt failed ──────────────────────▶ Failed
```

| Message status | Rule (checked in this order) |
|---|---|
| `Pending` | At least one recipient is `Pending`. |
| `Delivered` | All recipients are `Delivered`. |
| `Failed` | All recipients are `Failed`. |
| `PartiallySent` | Some recipients `Failed`, the others `Sent` or `Delivered`. |
| `Sent` | Otherwise: no failures, at least one recipient still waiting for confirmation. |

- `Delivered` and `Failed` are final. A recipient leaves `Pending` **once** and leaves `Sent` **once**.
- Delivery checks thin out over time: the next check waits half the time since sending (at least 10 s, at most 1 h). 24 h of tracking costs about 30 provider calls per recipient, not ~8,600 at a fixed 10 s interval.
- A `Sent` recipient that is not confirmed within 24 h stays `Sent` (the honest "unknown"), and the worker stops asking about it.
- All changes go through the `Message` aggregate: `message.RecordSendResult(...)` and `message.RecordDeliveryStatus(...)` update the recipient and recalculate the message status. One place for the rules; no other code sets a status.
- Max 3 attempts per recipient. Wait 2 s before the 2nd attempt and 4 s before the 3rd. Values are configuration.
- "Being sent right now" is **not** a status. The worker marks a recipient it works on with a short lock (`LockedUntil`). If the process crashes mid-send, the lock expires and the recipient is picked up again.

### Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-1 | `POST /messages` does not call the provider; it responds in well under 200 ms locally. |
| NFR-2 | Nothing is lost on a crash: the message is persisted before acceptance, and the worker resumes on restart. |
| NFR-3 | Structured logs include the message id and recipient id. Logs **never** contain the message body, and phone numbers are masked (`+97250****567`). |
| NFR-4 | `GET /health` reports API and database health. |
| NFR-5 | One command starts everything (`docker compose up`). |

---

## 4. Data Model (SQL)

```
Employees
  Id            int PK                   -- surrogate key used by EmployeePhones
  FirstName     text  NOT NULL           -- stored trimmed, inner spaces collapsed
  LastName      text  NOT NULL
  Address       text  NOT NULL
  UNIQUE (FirstName, LastName, Address)  -- case-insensitive

EmployeePhones                           -- one employee → many phone numbers
  Id            int PK
  EmployeeId    int FK -> Employees.Id
  PhoneNumber   text  NOT NULL           -- E.164
  UNIQUE (EmployeeId, PhoneNumber)       -- the same employee cannot repeat a number
  INDEX  (PhoneNumber)                   -- not unique: numbers may be shared between employees

Messages                                 -- one row per POST
  Id                 GUID PK             -- our id; used in URLs
  IdempotencyKey     text  NULL UNIQUE   -- the client's key for this POST
  Body               text  NOT NULL
  Status             text  NOT NULL      -- calculated from recipients, stored for fast filtering
  Version            int   NOT NULL      -- concurrency token, bumped on every change
  CreatedAt          datetime NOT NULL
  UpdatedAt          datetime NOT NULL
  INDEX (Status, CreatedAt, Id)          -- keyset paging with or without a status filter
  INDEX (CreatedAt, Id)

MessageRecipients                        -- one row per phone number = one SMS
  Id                 GUID PK             -- sent to the provider as `reference`
  MessageId          GUID FK -> Messages.Id
  PhoneNumber        text  NOT NULL      -- E.164
  Status             text  NOT NULL      -- Pending | Sent | Delivered | Failed
  Attempts           int   NOT NULL
  NextActionAt       datetime NOT NULL   -- when the worker looks next: send attempt (Pending) or delivery check (Sent)
  LockedUntil        datetime NULL       -- worker lock
  ProviderMessageId  text  NULL
  SentAt             datetime NULL       -- start of the 24 h delivery-check window
  LastError          text  NULL          -- short reason shown to the user; full details are in logs
  UNIQUE (MessageId, PhoneNumber)
  INDEX  (Status, NextActionAt)          -- worker query
```

**Why both `Id` and `IdempotencyKey`** (they do different jobs):

| | `Id` | `IdempotencyKey` |
|---|---|---|
| Who creates it | The server | The client |
| Always present | Yes | No, optional |
| Used for | Primary key, URLs, foreign keys | Only "have I seen this POST before?" |
| Trusted | Yes | No: any client can send any value, two clients could pick the same one |

Using the client's key as the primary key would let outside input decide our keys and would break when the header is missing.

**Why retry data (`Attempts`, `NextActionAt`, `LockedUntil`) is in the database and not in memory** (D15): an HTTP session belongs to a browser user and does not exist in a background worker. Plain memory is lost on crash or restart, which is exactly when this data is needed: to resume, to not exceed 3 attempts, and to release a lock. `Attempts` is also shown in the API.

---

## 5. Architecture

### 5.1 Solution layout

```
sms-service/
  src/
    Sms.Core/              Domain/       Message aggregate, MessageRecipient, statuses, PhoneNumber, MessageBody
                           Application/  Use cases (SendMessage, GetMessage, ListMessages, DispatchPending),
                                         interfaces IMessageRepository and ISmsProvider, SmsRequest / SendResult
                           No dependencies on other projects or packages.
    Sms.Infrastructure/    EF Core + SQLite, MessageRepository, employee seeding,
                           MockSmsProvider (adapter to the supplied provider), DispatcherWorker (BackgroundService).
    Sms.Api/               MessagesController, DI composition root, ProblemDetails, Swagger.
  tests/
    Sms.Core.Tests/        Unit tests: validation, normalization, statuses, use cases with fakes.
    Sms.Api.Tests/         Integration tests: WebApplicationFactory + SQLite in-memory + fake provider.
  web/                     React + TypeScript + Vite; Vitest + React Testing Library + MSW.
  seed/employees.json      Sample employees (A5).
  docker-compose.yml       api, web
  README.md
  SPEC.md
```

`Core` holds what is ours (rules and use cases); `Infrastructure` holds what is outside (database, provider); `Api` is the HTTP entry point. See D2 for why one `Core` project and not separate `Domain` + `Application`.

### 5.2 Dependency rule

```
Sms.Core  ◀──  Sms.Infrastructure  ◀──  Sms.Api (composition root)
```

`Core` never references outer projects. The provider and the database are behind interfaces, so the business rules are tested without HTTP or a database.

### 5.3 Request and send flow

1. `POST /messages` → `MessagesController.Send` maps the request and header to `SendMessageCommand(To[], Body, IdempotencyKey?)`.
2. `SendMessage`:
   - if the `IdempotencyKey` exists → return that message;
   - creates a **list** of `PhoneNumber` (each validated and normalized, duplicates removed) and one `MessageBody`;
   - creates the `Message` aggregate with one `MessageRecipient` per number, all `Pending`, saves it, returns the DTO. A unique-index violation on `IdempotencyKey` (two requests at the same moment) is caught and the existing message is returned.
3. `DispatcherWorker` runs every 2 s and calls `DispatchPending`:
   1. loads up to N **due** recipients (`Pending`, `NextActionAt <= now`, `LockedUntil` empty or past) with their message aggregate;
   2. sets `LockedUntil = now + 60 s` on them and saves (the lock);
   3. sends them **in parallel**, at most `MaxParallelSends` at a time (`Parallel.ForEachAsync`). This step only makes HTTP calls; it does not touch the database (EF Core's `DbContext` is not thread-safe);
   4. applies every result through `message.RecordSendResult(...)` and saves once, with the `Version` concurrency check.
   5. **Delivery check**, same pattern: loads `Sent` recipients with `NextActionAt <= now` and `SentAt` within 24 h, calls `ISmsProvider.GetDeliveryStatusAsync` in parallel, applies `message.RecordDeliveryStatus(...)`, and schedules the next check for those still unconfirmed (half the time since sending, 10 s – 1 h).
4. If the process dies between the provider call and the save, the recipient is still `Pending`. When the lock expires it is sent again **with the same `reference`** (the recipient `Id`), and the provider returns the same result without a second SMS (A3, D16).

With one worker (A6), the `Version` check is a safety net. Several API copies would need database row locking (PostgreSQL `FOR UPDATE SKIP LOCKED`) or a broker, see Part B.

### 5.4 Cross-cutting

- **Time:** use the built-in `TimeProvider` (not `DateTime.UtcNow`), so retries and locks are tested without waiting.
- **Errors:** one global exception handler maps `DomainValidationException` → 400, `MessageNotFoundException` → 404, anything else → 500 ProblemDetails (no stack trace in the response).
- **Configuration:** `appsettings.json` → strongly typed options, overridable by environment variables in Docker:

```json
"SmsProvider": { "BaseUrl": "http://localhost:5005", "SendPath": "/send",
                 "StatusPath": "/status/{providerId}", "TimeoutSeconds": 10 },
"Dispatcher":  { "IntervalSeconds": 2, "BatchSize": 50, "MaxParallelSends": 10,
                 "LockSeconds": 60, "MaxAttempts": 3, "FirstRetryDelaySeconds": 2,
                 "DeliveryCheckSeconds": 10, "DeliveryTrackingHours": 24 }
```

- **Logging:** built-in `ILogger` with structured parameters (`{MessageId}`, `{RecipientId}`), no extra logging library.

### 5.5 The provider boundary: generic model + provider DTOs

Two layers of types, so a provider change stays inside one class:

1. **Our generic model** (in `Sms.Core`, never changes when the provider changes):

```csharp
public interface ISmsProvider
{
    Task<SendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken);
    Task<DeliveryStatus> GetDeliveryStatusAsync(string providerMessageId, CancellationToken cancellationToken);
}

public sealed record SmsRequest(Guid Reference, string To, string Body);

public enum SendOutcome { Accepted, TransientFailure, PermanentFailure }

public sealed record SendResult(SendOutcome Outcome, string? ProviderMessageId = null, string? Error = null);

public enum DeliveryStatus { Unknown, Delivered, NotDelivered }   // Unknown = not confirmed yet, or the check failed
```

2. **Provider DTOs** (in `Sms.Infrastructure`, private to the adapter), shaped exactly like the provider's JSON:

```csharp
internal sealed class MockSmsProvider(HttpClient http, IOptions<SmsProviderOptions> options) : ISmsProvider
{
    private sealed record ProviderSendRequest(string To, string Body, string Reference);
    private sealed record ProviderSendResponse(string ProviderId);
    private sealed record ProviderStatusResponse(string Status);

    // SendAsync:               SmsRequest → ProviderSendRequest → HTTP → ProviderSendResponse / status code → SendResult
    // GetDeliveryStatusAsync:  providerId → HTTP GET → ProviderStatusResponse → DeliveryStatus
}
```

- The adapter is the **only** place that knows URLs, JSON field names and error codes. It maps HTTP 2xx → `Accepted`, 4xx → `PermanentFailure`, 5xx / timeout → `TransientFailure`.
- When the real contract is known, only the two DTOs and the mapping change. A new provider = a new adapter class + **one registration line** in `Program.cs`.
- One recipient per call: every provider supports it. A provider with a batch endpoint can get a batch method later (YAGNI now).
- **Name:** `ISmsProvider`, not `ISmsService`. It names what is behind it; "service" could mean anything, including our own use cases.

---

## 6. Design Decisions, Advantages, Disadvantages and Alternatives

Each decision lists the chosen option first (✅).

### D1. API style: Controllers, Minimal API, CQRS, MediatR

These are different things, not versions of one choice: **Controllers** and **Minimal API** are two ways to write HTTP endpoints. **CQRS** is a pattern: separate the code that changes data (commands) from the code that reads it (queries). **MediatR** is a library that sends a request object to its handler; people often use it to implement CQRS.

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Controllers with attributes + Swagger + light CQRS without a library** (`MessagesController` calls the command `SendMessage` and the queries `GetMessage`, `ListMessages` directly) | Familiar to almost every C# developer; each endpoint is a named method whose attributes (`[HttpPost]`, `[ProducesResponseType]`, `[Tags]`) document it; XML comments become the Swagger descriptions; Swagger UI is a ready way to try and debug the API; `[ApiController]` returns 400 for malformed input by itself. | A little more code than Minimal API; one package (Swashbuckle). |
| Minimal API | Least code. | Inline lambdas with long parameter lists read worse as the API grows; documenting them needs extension-method chains. Was the first choice, replaced for readability. |
| Full CQRS (separate read and write models / databases) | Reads and writes scale apart. | Two models to sync; far too much for 3 endpoints. |
| MediatR | Uniform handlers; pipeline behaviors. | Indirection: the endpoint does not show which code runs; an extra package; MediatR is commercially licensed from version 13 (2025; free tier for small companies; older versions stay free). |

**Recommendation:** Controllers with Swagger and command/query classes, no MediatR. Swagger is on in every environment because the system runs only locally. If the service grows to dozens of handlers with shared behaviors, reconsider MediatR or a free alternative.

### D2. Architecture and project naming

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **3 projects: `Sms.Core` (Domain + Application folders), `Sms.Infrastructure`, `Sms.Api`** | Compiler still keeps the database and HTTP out of business rules; fewer projects and references; "Core" is a widely used name. | The Domain ↔ Application boundary is by folder, not by compiler. Acceptable for this size. |
| 4 projects: `Domain`, `Application`, `Infrastructure`, `Api` | Strictest boundaries; classic Clean Architecture. | More projects than a 3-endpoint service needs. |
| Single project, vertical slices | Least files. | Nothing stops the database leaking into rules. |

Other common names for the inner project: `Domain`, `Core`, `Business`, `Model`. `Core` is clear and common.

### D3. Database: SQL or NoSQL, and which one

The data is relational: employee → phones, message → recipients. The task needs uniqueness rules (employee identity, idempotency key), safe status changes (transactions, concurrency), and filtering with paging. These are what SQL databases are built for. **Choice: SQL.**

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **SQLite (Part A)** via EF Core | Zero install, one file, perfect for "one command"; full SQL, unique indexes, transactions. | One writer at a time (fine at Part A volume, with WAL mode); not for many API copies. |
| **PostgreSQL** (recommended for production / Part B) | Free; strong concurrency; `FOR UPDATE SKIP LOCKED` makes the table a safe multi-worker queue; JSONB if flexible data is ever needed. | An extra container for Part A. |
| **SQL Server** | Very common in Microsoft / government shops; first-class EF Core support; good tooling. | Heavy container, slower start; license cost beyond Express. Choose it if the organization already standardizes on it. |
| MongoDB (document) | A message with its recipients is one natural document; scales out. | Unique rules across collections and multi-document transactions are harder; the employee ↔ phone relation fits SQL better. |
| Redis | Very fast; good as a cache or a simple queue. | Not a durable primary store for message history. |
| Cassandra / DynamoDB | Massive write throughput. | No joins, limited queries and uniqueness; overkill. |

**Recommendation:** SQLite for Part A, PostgreSQL (or the organization's SQL Server) for production. EF Core makes the switch a provider package + connection string + migrations, not a rewrite.

ORM: ✅ **EF Core** (migrations, LINQ, concurrency tokens, always parameterized SQL) vs **Dapper** (faster, explicit SQL, but hand-written mapping = more code).

### D4. Queue: database table, in-memory channel, RabbitMQ or Kafka

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Database table as the queue (outbox) + `BackgroundService`** | Nothing extra to run; the message and its "to send" state are saved in **one** transaction; survives crashes; retries in one place. | Polling delay (~2 s); at large scale polling and one SQLite writer become the bottleneck. |
| In-memory `Channel<T>` | Built into .NET; very fast. | Lost on crash; one process only. Fine as a local buffer, not as the queue. |
| **RabbitMQ** | Built for task queues: per-message acknowledge, redelivery, dead-letter queue, many competing consumers. **The right broker for sending SMS jobs** if one is needed. | Another service to run; and it does **not** remove the table: saving to the DB and publishing to RabbitMQ are two separate writes, so a crash between them loses or duplicates a job unless an outbox table relays it. |
| **Kafka** | Huge throughput, ordered log, replay of history. | Built for event streams, not jobs: no per-message ack or per-message retry/delay; consumers are limited by partitions; heavier to operate. A poor fit for "send each SMS once, retry some". |

**Why no broker in Part A:** with one process and up to 100 recipients per request, a broker adds a service and code but no capability we need, and we would still need the table. **Part B path:** keep the table as the outbox, add RabbitMQ when several workers or API copies must share the load.

### D5. Validation

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Value objects in `Core` (`PhoneNumber.Create`, `MessageBody.Create`)** | Rules exist in one place (DRY); an invalid `Message` cannot exist; trivial to unit test. | Errors must be mapped to ProblemDetails (one exception handler). |
| FluentValidation at the API | Expressive error messages. | Extra dependency; rules live outside the domain, so a future entry point could skip them. |
| DataAnnotations on DTOs | Built in. | Phone normalization does not fit attributes. |

UI validation repeats the two rules for UX only; the server stays the authority.

### D6. Retries

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **`Attempts` + `NextActionAt` per recipient, decided in the domain** | Persisted, survives restart; visible in the API; fully unit-tested. | We write the backoff (a few lines). |
| Polly / `Microsoft.Extensions.Http.Resilience` | Battle-tested; circuit breaker. | In-memory retries are lost on crash. Good **addition** for circuit breaking (Part B). |

### D7. Phone number storage

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Normalize to E.164** | One format for de-duplicating, filtering, sending. | Original formatting lost (not needed). |
| Store as typed | No conversion code. | `050-1234567` and `+972501234567` look like different numbers; duplicates slip through. |
| `libphonenumber-csharp` | Full international validation. | Dependency for an Israel-only rule. Switch if international numbers are needed. |

### D8. Pagination

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Keyset / cursor (`WHERE (CreatedAt, Id) < (last seen) ORDER BY CreatedAt DESC, Id DESC LIMIT n`)** | **Stable:** new messages arriving while the user pages (constant here, since the UI polls) never shift or duplicate rows. **Fast at any depth:** the index jumps straight to the cursor. No `COUNT(*)` on every request. | No "jump to page 7" and no total page count; the UI shows "Load more" instead. The cursor must encode a unique, stable sort key (`CreatedAt` + `Id` as tie-breaker). A few more lines than offset. |
| Offset (`page`, `pageSize`) | Simplest; page numbers and totals. | `OFFSET 10000` still reads and skips 10 000 rows; new rows push items to the next page, so the user sees duplicates or misses rows. |

**Recommendation:** keyset. The extra code is small (one cursor record, one `Where`), and it is the option that stays correct while messages keep arriving. The UI needs "Load more", not page numbers.

### D9. Frontend

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **React + TypeScript + Vite** | Fast dev server; types catch API contract mistakes; minimal config. | A build step. |
| Next.js | Routing, SSR. | SSR not needed for one internal screen. |
| Plain JavaScript | No compile step. | Contract mistakes show up at runtime. |

Data fetching: ✅ **TanStack Query** (`refetchInterval` for polling, loading/error states, `onSuccess` to clear the form) vs hand-written `fetch` + `useEffect` vs Redux (not needed). Forms: ✅ plain controlled inputs.

### D10. The SMS provider for development and tests

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Use the supplied mock; URL and paths in `appsettings.json`; in-memory `FakeSmsProvider` for automated tests** | Nothing extra to build; tests are fast and need no network. | Running end-to-end needs the supplied mock (we add it to `docker-compose.yml` once we see how it is delivered). |
| Build our own mock provider project | End-to-end runs before the real mock arrives. | Code the task already gives us. Only as an emergency fallback. |

### D11. Mapping and plumbing

- ✅ **Manual mapping** (`MessageDto.From(message)`) vs AutoMapper: explicit, debuggable, a few lines; AutoMapper hides errors until runtime and is commercially licensed from version 15.
- ✅ **No generic repository**: one `IMessageRepository` with the methods actually used.

### D12. Running with one command

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **`docker compose up`** | One command; same on every machine. | Needs Docker. |
| Script (`run.ps1`) | No Docker. | Needs .NET + Node installed. Fallback in the README. |

### D13. Message and recipient statuses

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Recipient: `Pending`, `Sent`, `Delivered`, `Failed`. Message: calculated, adds `PartiallySent`. UI label "Sending…" for `Pending`** | Every status means something; `Delivered` answers "did it really arrive?"; a mixed result is shown honestly; the user still sees "Sending…". | A delivery-check step and a second provider call (`GET /status`); a `Sent` recipient may never be confirmed (stays `Sent` after 24 h). |
| Store a real `Sending` status | The database says "in progress". | It lasts milliseconds; it needs its own transitions, and a crash leaves rows stuck in it unless more code releases them. The lock column already covers this, and the UI label gives the user the same information. |
| No `Delivered` (stop at `Sent`) | Less code. | Cannot tell "provider accepted" from "phone received", which is exactly what the task's failure question asks about. |
| Delivery webhooks instead of polling | No polling; instant updates. | Needs the provider to support callbacks and a public URL; the supplied mock only offers GET. Part B. |
| Message `Failed` when any recipient failed | No 5th message status. | Hides that most recipients got it. |

Message status is **stored** (recalculated by the aggregate on every change) rather than calculated in every query: the list filter becomes a simple `WHERE Status = ...`, and the rule lives in one C# method instead of being repeated as SQL.

### D14. Where errors are kept

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **`LastError` column (short reason) + full details in logs** | The API and UI can show "why it failed" per recipient; logs keep stack traces and HTTP details for developers. | Only the last error is kept on the row. |
| Logs only | No column. | The user cannot see why; logs are for operators, rotate, and are not queried by the API. |
| A `RecipientAttempts` history table | Full history of every attempt. | More code and data; useful for audits, not needed now. |

### D15. Where retry and lock data lives

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Database columns** | Survives crash and restart; visible in the API; one source of truth. | A few columns more. |
| Memory / session | No columns. | Lost exactly when needed (crash, restart). HTTP sessions do not exist in a background worker. |
| Broker (RabbitMQ redelivery count, delayed messages) | Built in. | Requires the broker (D4). |

### D16. Exactly-once sending

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **`Idempotency-Key` on POST + recipient leaves `Pending` once (concurrency token) + recipient `Id` as provider `reference`** | Double clicks never send twice. Retries and crashes end in one SMS per recipient **if** the provider de-duplicates `reference`. Each part is small and tested. | The crash case depends on the provider (A3, unverified). Without it the guarantee is at-least-once with a tiny duplicate window. |
| At-least-once only | Simplest. | A double click or crash can send twice. |
| At-most-once (mark sent **before** calling) | Never twice. | A crash loses the SMS silently. |
| Distributed transaction | Textbook guarantee. | HTTP providers do not support it. |

Our database and the provider cannot be updated in one transaction. If we crash after the provider accepted but before we saved `Sent`, only the provider knows. So we ask again with the same `reference` and let it answer "already sent".

**Recommendation:** the chosen option. Each of the three parts closes a different hole: the `Idempotency-Key` stops duplicate **requests**, the concurrency token stops duplicate **processing** on our side, and the `reference` stops duplicate **SMS** at the provider. Together they cost one unique column, one version column and one extra field in the provider call. **Trade-off:** the last part is outside our control; if the supplied mock ignores `reference`, the guarantee drops to "at-least-once with a tiny duplicate window" and the README must say so.

### D17. Sending to many numbers in parallel

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **`Parallel.ForEachAsync` with `MaxParallelSends` (configurable), HTTP calls only; results saved together afterwards** | Built into .NET; one readable loop; the limit protects the provider's rate limits; no thread-safety issues with `DbContext`. | One process only (A6). |
| `Task.WhenAll` on all recipients | Shortest code. | No limit: 100 calls at once can hit provider rate limits and exhaust sockets. |
| `Channel<T>` with N consumer tasks | Fine-grained control. | More code than a parallel loop for the same result. |
| Several workers / RabbitMQ consumers | Scales across machines. | Part B (D4). |

**Why this choice:** sending SMS is waiting on the network, not using the CPU, so the win comes from many calls in flight at once. `Parallel.ForEachAsync` gives exactly that in one loop with one number to tune (`MaxParallelSends`), and the limit is the key safety feature: providers enforce rate limits, and unbounded parallelism turns a slow provider into a flood of timeouts. Keeping the database work outside the parallel part avoids sharing a `DbContext` between threads (not allowed) without opening a scope per call. **Trade-off:** results of a batch are saved together, so a crash in the middle of a batch repeats that batch's sends; the `reference` de-duplication (D16) makes that safe. The test `DispatchPending_NeverExceedsMaxParallelSends` proves the limit with a fake provider that counts calls in flight.

### D18. Status code for `POST /messages`

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **`202 Accepted` + `Location`** | Accurate: accepted, processing continues; the client polls the `Location`. | Some clients expect `201` for "created". |
| `201 Created` | The resource does exist. | Suggests the work is done, while the SMS is not sent yet. |
| `200 OK` with the final result | Simple for the client. | Only possible if we send inline (rejected in D4). |

### D19. Recipients: phone numbers or employees

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **`to` is a list of phone numbers; employee tables are seeded reference data** | Matches the task's API; models the employee rules. | Employee tables are not used by the endpoints in Part A. |
| `employeeIds` in `POST /messages` | Send to all of an employee's phones. | Changes the contract; needs "which phones?" rules the task does not give. |

### D20. Employee uniqueness (First name + Last name + Address)

Terms:

- **Surrogate key:** an `Id` column with no business meaning (1, 2, 3…), created by the database. Other tables (`EmployeePhones`) point to it. It never changes, even if the employee's address changes.
- **Natural key:** the business values that identify the employee: First name + Last name + Address.
- **Unique index on the three normalized columns:** the database refuses a second row with the same three values. "Normalized" means the values are cleaned the same way before saving (trim, collapse double spaces, ignore upper/lower case), so `"Dana  Levi "` and `"dana levi"` count as the same person.

So the table has a simple `Id` for links **and** the database guarantees the business rule. Example: inserting `(Dana, Levi, Herzl 5 Rehovot)` twice fails on the second insert, whatever code tries it.

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **Surrogate `Id` + unique index on the three normalized columns** | The database enforces the rule; readable data; fields can be corrected without breaking links. | Values must be normalized consistently. |
| Hash of name + address as the key | One short column. | Same normalization issue; a hash of guessable values is reversible by trying; a typo fix changes the key and breaks phone links. |
| Keyed hash (HMAC) | Hides plain values. | Only if plain text is forbidden; adds key management. |
| Composite primary key (the three columns, no `Id`) | No extra column. | Every phone row repeats the three texts; an address change must update every linked row. |
| Uniqueness checked only in C# code | No database work. | Two requests at the same moment both pass the check and both insert; the rule is not really enforced. |

**Recommendation:** surrogate `Id` + unique index. It is the only option where the database itself guarantees the rule, links stay stable when details change, and no extra code is needed. Normalization lives in one place (an `EmployeeIdentity` value object), and the unique-index violation is the single source of "duplicate employee" errors.

### D21. Test tooling

| Option | Advantages | Disadvantages |
|---|---|---|
| ✅ **xUnit + hand-written fakes** | Readable, reusable, no mocking magic. | A few small fake classes. |
| Moq / NSubstitute | Less fake code. | Setup syntax hides intent; Moq had a telemetry controversy. |
| ✅ **WebApplicationFactory + SQLite in-memory** | Fast; tests the real HTTP pipeline and real SQL. | SQLite differs slightly from production DBs. |
| Testcontainers | Production-identical DB. | Needs Docker in tests. |
| ✅ **Vitest + React Testing Library + MSW** | Tests behavior as the user sees it. | Another toolchain. |

**Why this choice:**

- **xUnit** is the default in the .NET templates and ASP.NET Core's own tests; a new instance per test means no shared state between tests. NUnit and MSTest are equally capable; the choice is mostly convention.
- **Hand-written fakes over a mocking library:** our interfaces are tiny (`ISmsProvider` has two methods), so a fake is ~15 lines that any reader understands, and it can hold behavior a mock setup expresses badly (e.g. "fail twice, then accept", or counting calls in flight). Trade-off: a fake is code to maintain; if interfaces grow, NSubstitute becomes cheaper.
- **SQLite in-memory for integration tests:** runs the real SQL, unique indexes and concurrency tokens in milliseconds, with no Docker. Trade-off: it is not the production engine; when the project moves to PostgreSQL / SQL Server, the integration tests move to Testcontainers.
- **MSW in the UI:** components are tested against fake HTTP responses at the network level, so the real `fetch` code runs. Trade-off: one more tool; worth it only if UI tests are written (lowest priority, section 12).

---

## 7. TDD Approach

**Cycle:** Red → Green → Refactor, one behavior at a time. A production line is written only to make a failing test pass. Refactor only on green.

**Order (inside out):**

1. Domain value objects and the `Message` aggregate (pure, fastest feedback).
2. Use cases with fakes (`FakeMessageRepository`, `FakeSmsProvider`, `FakeTimeProvider`).
3. Infrastructure: repository against SQLite in-memory, `MockSmsProvider` against a stub `HttpMessageHandler`.
4. API contract tests (status codes, ProblemDetails, Location header, idempotency).
5. UI component tests (only if time allows).

**Initial test list:**

Core – domain
- `PhoneNumber_LocalFormatWithDash_IsNormalizedToE164`
- `PhoneNumber_LandlineOrTooShort_IsRejected`
- `MessageBody_EmptyOrWhitespace_IsRejected`
- `MessageBody_LongerThan500_IsRejected`
- `MessageBody_WithQuotesAndSqlWords_IsAccepted`
- `NewMessage_DuplicateNumbers_CreatesOneRecipientPerNumber`
- `NewMessage_AllRecipientsPending_StatusIsPending`
- `RecordSendResult_AllAccepted_MessageIsSent`
- `RecordSendResult_SomeAcceptedSomeFailed_MessageIsPartiallySent`
- `RecordSendResult_OnSentRecipient_Throws`
- `RecordSendResult_ThirdTransientFailure_MarksRecipientFailed`
- `RecordSendResult_TransientFailure_SchedulesExponentialRetry`
- `RecordDeliveryStatus_Delivered_AllRecipients_MessageIsDelivered`
- `RecordDeliveryStatus_NotDelivered_MarksRecipientFailed`
- `RecordDeliveryStatus_OnPendingRecipient_Throws`
- `MessageCursor_RoundTrip_ReturnsSameCreatedAtAndId`
- `MessageCursor_Garbage_IsRejected`

Core – use cases
- `SendMessage_SavesPendingMessage_AndDoesNotCallProvider`
- `SendMessage_OneInvalidNumber_RejectsWholeRequest`
- `SendMessage_SameIdempotencyKey_ReturnsOriginalMessage`
- `DispatchPending_SendsEachRecipientWithItsIdAsReference`
- `DispatchPending_NeverExceedsMaxParallelSends`
- `DispatchPending_SkipsRecipientsNotYetDue`
- `DispatchPending_SkipsLockedRecipients`
- `DispatchPending_ExpiredLock_IsPickedUpAgain`
- `DispatchPending_SentRecipient_IsCheckedUntilDelivered`
- `DispatchPending_SentOlderThan24h_IsNoLongerChecked`

Infrastructure
- `MockSmsProvider_MapsOurRequestToProviderJson`
- `MockSmsProvider_4xx_ReturnsPermanentFailure`
- `MockSmsProvider_5xxOrTimeout_ReturnsTransientFailure`
- `MessageRepository_DuplicateIdempotencyKey_IsRejectedByDatabase`
- `MessageRepository_StaleVersion_SaveFails`
- `EmployeeSeed_DuplicateNameAndAddress_IsRejected`

API
- `PostMessages_Valid_Returns202WithLocation`
- `PostMessages_RepeatedIdempotencyKey_ReturnsSameResponse`
- `PostMessages_InvalidSecondNumber_Returns400WithIndexedError`
- `PostMessages_SqlInjectionText_IsStoredVerbatim_AndTablesAreIntact`
- `GetMessage_UnknownId_Returns404`
- `ListMessages_FilterByStatus_ReturnsOnlyThatStatus`
- `ListMessages_UnknownStatus_Returns400`
- `ListMessages_NewMessageArrivesBetweenPages_NoDuplicatesOrGaps`
- `Health_DatabaseReachable_Returns200`
- `Health_DatabaseUnavailable_Returns503`
- `Logs_NeverContainBody_AndMaskPhoneNumbers` (captures log output with a test logger provider)

**Coverage goal:** every rule in `Core` has a test. No line-percentage target.

### 7.1 The task's "automated tests for the critical logic" requirement

The task requires automated tests for the **critical part of the logic**. That is different from monitoring: tests run **before** the code ships and prove the rules are right; monitoring (section 11) runs **in production** and shows that the system is healthy. Critical = a bug here sends a wrong SMS, sends it twice, loses it, or leaks data:

| Critical logic | Why critical | Covered by |
|---|---|---|
| Phone validation and normalization | A wrong number = SMS to the wrong person | `PhoneNumber_*`, `PostMessages_InvalidSecondNumber_*` |
| Body validation | Empty or oversized SMS; injection safety | `MessageBody_*`, `PostMessages_SqlInjectionText_*` |
| Recipient de-duplication | Same person gets the SMS twice | `NewMessage_DuplicateNumbers_*` |
| Status rules (recipient and message) | Users and support act on wrong information | `RecordSendResult_*`, `RecordDeliveryStatus_*` |
| Retries and max attempts | Endless retries, or giving up too early | `RecordSendResult_ThirdTransientFailure_*`, `*_SchedulesExponentialRetry` |
| Exactly-once (idempotency, lock, `reference`) | Duplicate SMS, double charges | `SendMessage_SameIdempotencyKey_*`, `DispatchPending_*Lock*`, `*_StaleVersion_*`, `DispatchPending_SendsEachRecipientWithItsIdAsReference` |
| Parallel limit | Flooding the provider, rate-limit bans | `DispatchPending_NeverExceedsMaxParallelSends` |
| Provider error mapping | A permanent error retried forever, or a transient one given up | `MockSmsProvider_4xx_*`, `MockSmsProvider_5xxOrTimeout_*` |
| Paging stability | Messages missing from the list | `ListMessages_NewMessageArrivesBetweenPages_*` |

The parts monitoring relies on are also tested automatically: the health endpoint (`Health_*`) and log privacy (`Logs_NeverContainBody_*`). Alerts and dashboards themselves are configuration in a monitoring tool and are verified there, not by unit tests.

---

## 8. Code Quality Rules

### SOLID in this design

| Principle | Where |
|---|---|
| **S** – Single responsibility | `PhoneNumber` validates phones; `SendMessage` accepts; `DispatchPending` sends; `MockSmsProvider` translates to the provider; the controller only maps HTTP ↔ use case. |
| **O** – Open/closed | A new provider is a new `ISmsProvider` class; use cases do not change (5.5). |
| **L** – Liskov substitution | `FakeSmsProvider`, `MockSmsProvider` and future providers return the same `SendResult` and throw nothing extra. |
| **I** – Interface segregation | `ISmsProvider` has the two provider operations we use (send, delivery status); `IMessageRepository` only the methods in use. |
| **D** – Dependency inversion | `Core` owns the interfaces; `Infrastructure` implements them; wiring only in `Program.cs`. |

### DRY

- Validation rules: only in the value objects (UI copy for UX is the stated exception).
- Status rules: only in the `Message` aggregate.
- Error → HTTP mapping: only in the global exception handler.
- Provider format ↔ our model: only in the provider adapter.
- Constants: in options classes, no magic numbers.

### Readability and minimal code

- Names say intent (`RecordSendResult`, not `Update`); no abbreviations.
- Small methods, early returns, no nested `if` pyramids.
- `record` types for DTOs and commands; nullable reference types on; warnings as errors.
- `async` all the way with `CancellationToken`.
- Comments explain **why**, never what.
- No code "for later".
- `dotnet format` and ESLint + Prettier keep style automatic.

---

## 9. Security

- **SQL injection is prevented by parameterized queries, not by filtering text.** All database access goes through EF Core LINQ, which always sends values as parameters. Raw SQL is not used; if it ever is, only `FromSql` / `ExecuteSql` with interpolated parameters, never string concatenation.
- **We do not reject "SQL-looking" text** in the message body. A message like `Don't forget: meeting at 10; bring the "OR 1=1" report` is legitimate, and blacklists are both easy to bypass and break real messages. The test `PostMessages_SqlInjectionText_IsStoredVerbatim_AndTablesAreIntact` proves the protection.
- **XSS:** React escapes all text by default; `dangerouslySetInnerHTML` is not used.
- **Input limits:** body length, recipients per request (A8), request size limit in Kestrel.
- **Sensitive data:** no message bodies in logs, masked phone numbers (NFR-3); no secrets in the repository (provider URL/keys from configuration / environment).

---

## 10. Failure Handling (input for Part B)

| Scenario | Behavior in this design | Future improvement |
|---|---|---|
| Server crashes mid-send | Recipients are stored and still `Pending`. When the lock expires they are sent again with the same `reference`; the provider does not send twice (D16). | — |
| User clicks Send twice | Button disabled while sending; one `Idempotency-Key`; the server returns the original message. | — |
| Provider says success but SMS not delivered | Recipient is `Sent` until the delivery check gets an answer: `Delivered`, or `Failed` with "not delivered". Unconfirmed after 24 h → stays `Sent` and is visible by filter. | Delivery webhooks instead of polling; alert on old unconfirmed `Sent` recipients. |
| Provider is down | Transient errors → retry with backoff; then `Failed` with `LastError`. API keeps accepting. | Circuit breaker; failover to a second provider. |
| Hundreds of thousands of recipients | Not supported: SQLite single writer, one worker, 100 recipients per request. | PostgreSQL, RabbitMQ with outbox relay, many workers, bulk import endpoint, provider rate limits. |

## 11. Monitoring (minimal for Part A)

- `GET /health` (API + DB check).
- Structured logs with `MessageId`, `RecipientId`, `Status`, `Attempts`, provider latency.
- `GET /messages?status=Failed` and `?status=PartiallySent` are quick manual checks.
- Automated tests cover the health endpoint and log privacy (section 7.1).
- Future: OpenTelemetry metrics (sent / failed / retry counts) with alerts on failure rate and on the age of the oldest `Pending` recipient.

---

## 12. Delivery Plan (≤ 4 hours)

| Step | Time |
|---|---|
| Solution skeleton, Docker Compose | 20 min |
| Core domain (aggregate, value objects, statuses) + tests | 45 min |
| Core use cases (send, get, list with cursor, dispatch with parallelism, delivery check) + tests | 60 min |
| Infrastructure (EF Core, seed, provider adapter, worker) + API + integration tests | 55 min |
| UI (form + table) | 35 min |
| README (decisions, assumptions, not done, time spent, AI usage) | 15 min |
| **Total** | **~3 h 50 min** |

Multi-recipient support, keyset paging and delivery checks add work; building no mock provider gives some back. The plan has no slack. Docker Compose is the biggest time risk; if it stalls, ship the `run.ps1` fallback and note it in the README.

If time runs out, drop in this order: UI tests → UI → delivery check (`Delivered`) → employee seed. The API, validation, persistence, idempotency, the worker and their tests are not dropped.

## 13. Deliverables Checklist

- [ ] Repository (or zip) that starts with `docker compose up`
- [ ] README: what was built, decisions, assumptions, what was not done and why, what to add with more time, **actual time spent**, **AI usage declaration**
- [ ] This SPEC (required because AI was used)
- [ ] Automated tests passing (`dotnet test`, `npm test`)
- [ ] Part B document (1–2 pages, referencing specific classes from this design)

## 14. AI Usage Declaration

This SPEC was drafted with AI assistance (Claude) from the task description, then reviewed, edited and owned by the candidate. The code is developed from this SPEC; AI-assisted parts are declared in the README.
