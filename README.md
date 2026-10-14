# Notification Processing API

A small ASP.NET Core (.NET 9) Web API that accepts, validates, stores and processes **Email** notifications.
Storage is SQL Server (EF Core). Failed sends are retried with back-off, and crashed runs are recovered.

## 1. How to run

```bash
cd D:\Codingtest
dotnet run --project NotificationApi --urls http://localhost:5235
```

Open **http://localhost:5235/swagger** (the root URL redirects there).

**Database:** SQL Server. The connection string is in `NotificationApi/appsettings.json`
(default `Server=.\SQLEXPRESS;Database=NotificationDb;Trusted_Connection=True;TrustServerCertificate=True`).
On start the app creates the database if needed and applies the EF migrations (`Database.Migrate()`),
so no manual step is needed. `Database/CreateTables.sql` is the same schema as an idempotent script
(generated from the migrations) if you prefer to run it yourself in SSMS on an empty database.

**Tests:**

```bash
dotnet test
```

* Unit tests (validator, service, worker) need nothing.
* SQL Server tests (repository + API) create a temporary database `NotificationApi_Test_<guid>` on `.\SQLEXPRESS`,
  run the real migrations, and delete it afterwards. Use another server with the environment variable
  `NOTIFICATION_TEST_SQL` (connection string without `Database=`). If no SQL Server answers, those tests are **skipped**, not failed.

| Method | URL | Purpose |
|--------|-----|---------|
| POST | `/api/notifications` | Create a notification (201, or 200 if duplicate, 400 if invalid). Optional header `Idempotency-Key` |
| GET | `/api/notifications/{id}` | Get one notification (404 if not found) |
| GET | `/api/notifications/customer/{customerId}?status=&page=1&pageSize=20` | Customer's notifications, newest first, paged, optional status filter |
| POST | `/api/notifications/process` | Process everything that is due now |

Tip: a recipient that starts with `fail` (e.g. `fail@example.com`) makes the simulated provider throw, so you can see retries and `Failed`.

### Settings (`appsettings.json`)

| Setting | Default | Meaning |
|---------|---------|---------|
| `Notifications:DuplicateWindowMinutes` | 5 | Same request inside this window is a duplicate |
| `Notifications:MaxAttempts` | 3 | Send attempts before a notification becomes `Failed` |
| `Notifications:RetryBaseDelaySeconds` | 30 | Wait before attempt 2; doubles each time (30s, 60s, 120s...) |
| `Notifications:ProcessingTimeoutMinutes` | 5 | `Processing` for longer than this = crashed, pick it up again |
| `Notifications:BatchSize` | 100 | Max notifications per processing run |
| `Notifications:WorkerEnabled` | **false** | `true` = background worker processes pending notifications automatically |
| `Notifications:WorkerIntervalSeconds` | 10 | How often the worker runs |
| `Auth:ApiKey` | **empty (off)** | If set, every `/api` call needs header `X-Api-Key: <value>` (Swagger has an Authorize button) |

The worker and API key are off by default so a reviewer can create notifications, look at them as `Pending`, and call `/process` by hand.

## 2. Design

```
Controller  ->  Service  ->  Repository (interface)   -> SqlServer (EF Core)  /  InMemory (unit tests)
                   |
                   +------>  Provider   (interface)   -> SimulatedEmailProvider
                   +------>  Validator  (interface)
                   +------>  TimeProvider (built-in clock)

BackgroundService (optional) -> Service.ProcessPendingAsync
ApiKeyMiddleware (optional)  -> protects /api
```

* **Controllers**: only HTTP concerns (status codes, mapping to DTOs, paging checks).
* **Services** (`NotificationService`): business rules - validation, duplicates, processing order, retry/failure handling.
* **Repositories**: persistence behind `INotificationRepository`. `SqlServerNotificationRepository` is the real one. `InMemoryNotificationRepository` keeps unit tests fast.
* **Provider**: `INotificationProvider` is the external-system boundary. The simulated one just logs.
* **Validation**: small hand-written validator returning `field -> messages`, so every 400 has one consistent shape (`ValidationProblemDetails`).
* **Dependency injection**: everything through constructors; wiring in `Program.cs`. Settings are bound to `NotificationOptions` / `AuthOptions`.
* **Clock**: `TimeProvider` is injected so tests move time without sleeping.

### Status flow

```
Pending --claim--> Processing --success--> Sent
                       |
                       +--failure, attempts left--> Pending (NextAttemptAt = now + back-off)
                       +--failure, no attempts left--> Failed
                       +--stuck > timeout, attempts left--> claimed again
                       +--stuck > timeout, no attempts--> Failed
```

### Business rules - how they are done

* **High priority first**: claim query orders by `Priority DESC`, then `CreatedAt` (oldest first for the same priority).
* **Duplicates**: a SHA-256 fingerprint of customer, type, recipient, subject, message, priority and requested time (customer/recipient trimmed and lower-cased). Same fingerprint inside the window -> existing notification returned with **200**. With an `Idempotency-Key` header, the key (per customer) is the identity instead, so the caller decides what "same" means. The check + insert run under `sp_getapplock` inside a transaction, so parallel retries (even on several app instances) create one row.
* **Provider failure**: caught per notification; the rest of the batch continues. The reason is stored in `FailureReason`. Attempts left -> back to `Pending` with exponential back-off in `NextAttemptAt`; no attempts left -> `Failed`.
* **No double sending**: claiming uses `UPDLOCK, READPAST` in a transaction, so parallel `/process` calls or workers never take the same row.
* **Crash recovery**: a row in `Processing` longer than the timeout is claimed again (or marked `Failed` if attempts are used up).
* **Cancellation**: if the request is cancelled mid-batch, unsent items go back to `Pending` and the attempt is not counted.
* **Future delivery time**: waits as `Pending` until `NextAttemptAt <= now`.
* **Authentication**: optional API key, compared in constant time; `/swagger` stays open.

## 3. Assumptions

* Only `Email` exists. `type` is still required, and the email check applies to `Email`.
* `priority` is optional (default `Normal`); `requestedDeliveryTime` is optional (default "now").
* `subject` max 200 chars; `message` max 5000; `Idempotency-Key` max 100.
* Delivery time **exactly equal to now is allowed**; only strictly earlier is "in the past".
* A date without time (e.g. `"2030-10-06"`) is accepted and read in the server's time zone; send `...T00:00:00Z` to be explicit.
* "Valid email" = bare address with a dotted domain. `Name <a@b.com>` and `user@localhost` are rejected.
* The `RequestedDeliveryDate` column (date only, e.g. `2030-10-06`) is computed by SQL Server for easy reading; the logic uses the full `RequestedDeliveryTime`.

## 4. Trade-offs

* Delivery is **at-least-once**: if a run is slower than `ProcessingTimeoutMinutes` but still alive, another run may send the same notification again. A real provider call should be made idempotent too.
* Retry state lives in the same table (`NextAttemptAt`, `AttemptCount`) - simple and visible in SSMS, but not a real queue.
* A message broker (RabbitMQ/Kafka) was **not** used: the table plus the locking above is enough at this scale, and it keeps setup to one dependency.
* Hand-written validator instead of FluentValidation: no extra dependency for ~10 rules.
* API key auth is deliberately minimal (one shared key).
* The worker polls the database; it does not push.

## 5. What I would improve for production

* A real queue or outbox pattern with a dedicated sender service for high volume.
* Polly (circuit breaker) around the real provider, plus a dead-letter/alert path for `Failed`.
* Proper auth (JWT / OAuth), per-client rate limiting, and secrets from a vault instead of `appsettings`.
* Idempotency keys stored with the request hash, returning 422 when the same key is reused with different content.
* Structured logging, metrics (queue depth, failure rate), health checks.
* Archive/cleanup of old notifications; a unique index strategy for fingerprints.

## 6. Unclear requirements and my interpretation

* **"Exact same request"**: same business content (not the same HTTP call), or the same `Idempotency-Key` when given.
* **What to return for a duplicate**: 200 with the original notification (idempotent), not an error, so the retrying client is happy.
* **"Process pending notifications"**: only those that are *due*; future-dated ones stay pending. Failed-but-retryable ones come back after their back-off.
* **Provider failure status**: `Pending` with a later `NextAttemptAt` while attempts remain, then `Failed`.
