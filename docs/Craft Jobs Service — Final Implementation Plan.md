# Craft Jobs Service — Final Implementation Plan

Oct 14, 2026 · @Saba · supersedes v1 (`Craft Jobs Service — Implementation Plan.md`)

> **Status (Oct 14, 2026):** every build-order step is implemented. `dotnet build` is clean in Debug and Release (0 warnings, 0 errors) and no packages are vulnerable. All 38 tests pass, and the exactly-once contention test passed 5 runs in a row. Implementation differs from this plan in a few places; see `README.md` and `docs/ai-log.md` (text instead of jsonb for stored responses, validation kept in the domain, plain SQL for claim and retry, Testcontainers 4.16, migrations bundle instead of a `--migrate` flag).

## 1. Review of v1: what changes and why

The v1 design holds: one invariant (exactly one invoice per completed job), DB-as-queue with `SKIP LOCKED`, lease + fencing, ETag/If-Match, Idempotency-Key, separate API and worker. Changes below fix bugs, a flaky test, and a test that would not prove what it claims.

### Correctness fixes

1. **EF Core Guid key pitfall.** Domain creates ids with `Guid.CreateVersion7()`. EF's default for Guid keys is `ValueGeneratedOnAdd`, so a new `JobLine` or `ExportTask` found through a navigation with its key already set is tracked as **Modified**, not Added (EF Core 3.0+ behaviour). The `UPDATE` hits 0 rows and throws `DbUpdateConcurrencyException`, so every add-line and complete would return a false 412. Fix: `ValueGeneratedNever()` on every Guid key.
2. **Count attempts at claim time.** v1 increments `attempts` only on handled failures, so a task that crashes its worker is reclaimed forever. Fix: the claim sets `attempts = attempts + 1`; a claimed task with `attempts > MaxAttempts` is dead-lettered without calling accounting.
3. **Batch tail can outlive the lease.** v1 claims 10 and processes them one by one; 10 slow calls can exceed the 30 s lease, so tail tasks get taken over and called twice (safe, but wasted). Fix: process the claimed batch concurrently (`Parallel.ForEachAsync`, one DbContext per task), so one lease covers one call.
4. **Fake accounting must be race-safe and independent.** Check-then-insert races between a stale and a current lease holder and throws a unique violation instead of replaying. Fix: `INSERT … ON CONFLICT (idempotency_key) DO NOTHING RETURNING id`, then select the existing row. Use its own connection (`NpgsqlDataSource`) so its write commits independently of the worker's transaction, like a real external system.
5. **Worker's job update goes through the domain.** `MarkExported()` / `MarkExportFailed()` only from Completed, and they bump `Version`, so the ETag changes when export finishes.
6. **Fenced writes also check `status = 'Processing'`.**
7. **Draft → InProgress was undefined.** Decision: the first `AddLine` on a Draft job moves it to InProgress. `Complete` is allowed only from InProgress and requires at least one line (lines can be deleted, so InProgress does not imply lines).
8. **Money precision.** `decimal` accepts `1.234` but `numeric(18,2)` silently rounds it, so the response total differs from the stored one. Validate scale at the API (unit price ≤ 2 decimals, quantity ≤ 3). Line total = `Math.Round(qty * price, 2, MidpointRounding.AwayFromZero)`; job total = sum of line totals.
9. **Idempotent replay must return headers too.** Store `response_etag`; derive `Location` from `job_id`. Hash the re-serialized bound DTO, not raw bytes, so whitespace or key order do not cause a false 422.
10. **Snake_case names in raw SQL need a naming convention.** Use `EFCore.NamingConventions` (`UseSnakeCaseNamingConvention()`), and alias raw SQL result columns explicitly.

### Test fixes

11. **v1 test 1 is flaky.** With 20 % failures and MaxAttempts 5, P(one task dead-letters) = 0.2⁵ ≈ 0.03 %; across 200 tasks, a run fails "all Succeeded" about 6 % of the time. Production backoff (2, 4, 8, 16 s) also makes it slow. Fix: test options MaxAttempts 50, backoff base 10 ms, cap 200 ms.
12. **v1 test 1 never exercises the idempotency key.** Failures before the invoice is written never cause a replay, and "no duplicate keys" is guaranteed by the unique constraint itself, so it is tautological. Fix: the fake supports **fail-before** and **fail-after** (invoice written, response "lost"). The test asserts replay count > 0 and exactly one invoice per **job_id**. A 500 ms lease with latency up to 600 ms forces real lease takeovers under contention.
13. **Test 2 without sleeping.** Expire the lease with `UPDATE export_tasks SET lease_until = now() - interval '1 second'`.
14. **One Postgres container for the whole suite** (`ICollectionFixture`), truncating between tests.
15. **Tests interleaved with build steps** (domain tests in step 1, test 3 right after the API), so each step ends with proof that it works.

### Stack and tooling updates

16. FluentAssertions v8+ needs a commercial licence → **Shouldly** (MIT).
17. .NET 9+ templates dropped Swashbuckle → `Microsoft.AspNetCore.OpenApi` (`/openapi/v1.json`) + `Swashbuckle.AspNetCore.SwaggerUI` at `/swagger`. Bind headers with `[FromHeader]` so `If-Match` and `Idempotency-Key` show up in OpenAPI.
18. .NET 10 built-in minimal API validation (`AddValidation()` + DataAnnotations) instead of hand-rolled checks.
19. **Migrations outside the app** *(revised during implementation)*: `dotnet ef database update` locally, an EF Core migrations bundle (`src/Jobs.Core/Migrations.Dockerfile`) as the compose / deployment step. There is no migrate flag and no startup migration, not even in production. EF Core 9+ takes a migration lock, so "races" is no longer the main argument; a separate step still fails fast, keeps DDL in one place, can be reviewed as an idempotent script, and allows a least-privilege app user.
20. Fenced complete/fail use EF `ExecuteUpdateAsync` (type-safe). Raw SQL only for the claim (`FOR UPDATE SKIP LOCKED`).
21. `postgres:17-alpine` in compose and Testcontainers (the 18 image changed its volume layout; avoid the surprise).
22. Console logging with `IncludeScopes = true`, otherwise TaskId/JobId scopes are invisible.
23. `POST /jobs/{id}/lines` returns **200 + job body + ETag** (not 201): lines are changed through the job aggregate, and the ETag then describes the returned representation.

### Scope change

24. **Line edit and delete move into scope** (v1 had them as next steps): `PATCH /jobs/{id}/lines/{lineId}` and `DELETE /jobs/{id}/lines/{lineId}`, both guarded by the **job's** If-Match and both bumping `Job.Version`. Removing the last line leaves the job InProgress, so "Complete needs at least one line" is now a real runtime check, not just defence in depth.

---

## 2. Business summary

Turns finished field work into exactly one invoice in the customer's accounting system, for small trade companies (electricians, plumbers, carpenters).

Flow: office creates a **job** → workers add, correct or remove **lines** (labour/material, quantity × unit price) → someone **completes** it (frozen) → a background worker **exports** the invoice, retrying while accounting is down.

**Lifecycle:** Draft → (first line) InProgress → Completed → Exported, or ExportFailed after the retry budget.

| Rule | Protected by |
| --- | --- |
| Every completed job produces exactly one invoice | Unique export task per job, lease token fencing, idempotency key on the external call |
| Two editors never silently overwrite each other | `Version` column, ETag / If-Match, 412 |
| A retried create never duplicates a job | Idempotency-Key header |
| Lines are added, edited or deleted only in Draft / InProgress | Domain state machine |
| Two people changing lines of the same job never overwrite each other | Line changes bump `Job.Version`; same If-Match / 412 path |
| Complete needs at least one line | Domain validation |
| An accounting outage never loses an export | Retry with backoff, dead-letter, visible export status |

---

## 3. Scope

**In scope:** jobs + lines with add, edit and delete; state machine; the eight endpoints; Idempotency-Key on create; mandatory If-Match on writes; worker with SKIP LOCKED claim, lease, fencing, backoff + jitter, dead-letter; fake accounting with idempotency and fail-before/fail-after injection; tests 1–6; docker-compose with `--scale worker=3`; README; `requests.http` demo file; `/health`.

**Simplified:** list returns a fixed 50, newest first; idempotency has no "in progress" state (the unique index serialises duplicates); fake accounting shares the database in its own schema; single currency; PATCH (job and line) uses `application/json` with null = unchanged (not JSON Merge Patch); lines are hard-deleted (no audit trail); one API instance in compose.

**Out of scope (next steps):** RabbitMQ + outbox relay + inbox; auth and multi-tenancy; keyset paging; line change history / soft delete; OpenTelemetry; idempotency key TTL; dead-letter replay; lease heartbeat; `If-None-Match`/304; CI.

**Decisions**

| Topic | Decision | Alternative named in the README |
| --- | --- | --- |
| Concurrency token | Explicit `int Version`, starts at 1, bumped in domain methods | Postgres `xmin` |
| Version scope | Job is the aggregate root; adding, editing or deleting a line bumps `Job.Version` | Per-line versions |
| Version transport | ETag / If-Match; 428 missing, 412 stale | Version in the JSON body |
| Database | PostgreSQL 17 | SQL Server with `READPAST` / `rowversion` |
| Queue | `export_tasks` claimed with `FOR UPDATE SKIP LOCKED` | RabbitMQ |
| Worker coordination | Lease + lease token (compare-and-set) | Redis lock |
| Ids | `Guid.CreateVersion7()` in the domain, `ValueGeneratedNever()` | DB-generated |
| Migrations | `dotnet ef database update` locally; migrations bundle as a one-shot `migrate` step | Migrate at startup (EF 9+ lock makes it workable; still not recommended for production) |
| Assertions | Shouldly | FluentAssertions (commercial licence from v8) |

---

## 4. Architecture and solution structure

Two deployables (API, worker) share one domain/persistence project and one PostgreSQL database. The database is the system of record and the work queue, so a job's completion and its export task commit in one transaction (no dual write, no relay).

**Stack and packages** (versions pinned in `Directory.Packages.props`)

- .NET 10, ASP.NET Core minimal APIs, `AddValidation()`, `AddProblemDetails()`
- `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x, `EFCore.NamingConventions` (check a 10.x release exists; if not, set names explicitly), `Microsoft.EntityFrameworkCore.Design` (API only)
- `Npgsql.DependencyInjection` (`AddNpgsqlDataSource`), shared by EF and the fake accounting client
- `Microsoft.AspNetCore.OpenApi`, `Swashbuckle.AspNetCore.SwaggerUI`, `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`
- Tests: `xunit.v3`, `Testcontainers.PostgreSql`, `Microsoft.AspNetCore.Mvc.Testing`, `Shouldly`

```
global.json                    SDK 10.0.4xx, rollForward latestFeature
Directory.Build.props          net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors
Directory.Packages.props       central package versions
CraftJobs.slnx
docker-compose.yml
requests.http                  demo flow (create → get → patch → line → complete → get)
src/Jobs.Core
  Domain/          Job, JobLine, LineType, JobStatus, ExportTask, ExportTaskStatus,
                   JobStateConflictException (409), DomainValidationException (400)
  Persistence/     JobsDbContext, Configurations/, Migrations/,
                   FakeAccounting/FakeInvoice (schema fake_accounting, simulation only)
  DependencyInjection.cs   AddJobsPersistence(connectionString)
src/Jobs.Api
  Program.cs       OpenAPI + Swagger UI, ProblemDetails, /health (no migration code)
  Endpoints/JobEndpoints.cs
  Contracts/       request / response records
  Http/            ETags (format/parse via EntityTagHeaderValue), Idempotency, ProblemTypes,
                   DomainExceptionHandler (IExceptionHandler)
  Dockerfile
src/Jobs.Worker
  Program.cs       Host.CreateApplicationBuilder, JSON console with scopes
  ExportWorker.cs        BackgroundService: loop, delay only when idle
  ExportProcessor.cs     ProcessBatchAsync(workerId): claim → call → complete/fail
  TaskClaimer.cs         ClaimAsync (raw SQL), CompleteAsync / FailAsync / DeadLetterAsync (fenced)
  ExportWorkerOptions.cs, Backoff.cs
  Accounting/            IAccountingClient, FakeAccountingClient, FakeAccountingOptions,
                         TransientAccountingException, PermanentAccountingException
  Dockerfile
tests/Jobs.Tests
  Domain/JobTests.cs
  Integration/     PostgresFixture, ApiFactory (WebApplicationFactory<Program>),
                   ConcurrencyTests, IdempotencyTests, ExportTests
docs/ai-log.md                 running AI usage log
README.md
```

**Design rules**

- No CQRS, MediatR or repositories; `DbContext` used directly.
- Business rules live on `Job`; every mutating method validates state and bumps `Version`.
- `Complete` creates the `ExportTask` in the same `SaveChanges`.
- Raw SQL only for the claim. Fenced writes use `ExecuteUpdateAsync`.
- Errors are ProblemDetails with stable `type` values.
- Time comes from `TimeProvider` (domain timestamps); lease times use the database's `now()`.
- `BackgroundService` is a singleton, so the worker uses `IDbContextFactory<JobsDbContext>`.

---

## 5. Domain model

```
Job.Create(customer, site, description, now)   → Draft, Version = 1
job.Update(customer?, site?, description?, now) → Draft/InProgress only; Version++
job.AddLine(type, description, qty, price, now) → Draft/InProgress only; Draft → InProgress; Version++
job.UpdateLine(lineId, type?, description?, qty?, price?, now) → Draft/InProgress only; unknown line → LineNotFound; Version++
job.RemoveLine(lineId, now) → Draft/InProgress only; unknown line → LineNotFound; status unchanged; Version++
job.Complete(now)       → InProgress only, ≥ 1 line; → Completed, CompletedAt, creates ExportTask; Version++
job.MarkExported(now)   → Completed only; → Exported; Version++
job.MarkExportFailed(now) → Completed only; → ExportFailed; Version++
job.Total               → Σ Math.Round(qty × price, 2, AwayFromZero)
```

Validation: customer name and site address required (≤ 200 chars), description ≤ 2000, quantity > 0 with ≤ 3 decimals, unit price ≥ 0 with ≤ 2 decimals. The same line rules apply to `UpdateLine`; an update with no fields is a 400.

Lines are mutated only through `Job` (private `List<JobLine>` backing field, `JobLine` setters internal). `RemoveLine` takes the line out of the collection; with a required FK, EF deletes the orphan row in the same `SaveChanges` as the job's version bump.

---

## 6. Data model

Snake_case via naming convention. Enums stored as strings (`varchar(32)`). Timestamps `timestamptz`, written as UTC `DateTimeOffset`.

| Table | Columns | Key constraints / indexes |
| --- | --- | --- |
| `jobs` | id uuid, customer_name, site_address, description, status, version int, created_at, updated_at, completed_at | PK id; `version` is `IsConcurrencyToken()`; index (status, created_at desc) |
| `job_lines` | id, job_id, type (Labour/Material), description, quantity numeric(18,3), unit_price numeric(18,2), created_at | FK job_id cascade; checks `quantity > 0`, `unit_price >= 0` |
| `export_tasks` | id, job_id, status (Pending/Processing/Succeeded/DeadLettered), attempts, next_attempt_at, lease_token uuid null, locked_by, lease_until, last_error, external_invoice_id, created_at, completed_at | **Unique job_id**; index (status, next_attempt_at) |
| `idempotency_keys` | key varchar(100), request_hash, response_status, response_body jsonb, response_etag, job_id, created_at | **PK key** |
| `fake_accounting.invoices` | id, idempotency_key, job_id, total numeric(18,2), created_at | **Unique idempotency_key** |

All Guid keys: `ValueGeneratedNever()`. `jobs.status` mirrors the export outcome so `GET /jobs/{id}` is one read plus one join.

---

## 7. API

| Endpoint | Guard | Success | Errors |
| --- | --- | --- | --- |
| `POST /jobs` | Idempotency-Key required | 201 + ETag + Location | 400 validation or missing key, 422 key reused with a different body |
| `GET /jobs/{id}` | none | 200 + ETag; lines, total, export status | 404 |
| `GET /jobs?status=` | none | 200, newest first, limit 50 | 400 unknown status |
| `PATCH /jobs/{id}` | If-Match | 200 + new ETag | 400, 404, 409 frozen, 412, 428 |
| `POST /jobs/{id}/lines` | If-Match | 200 + job body + new ETag | 400, 404, 409 frozen, 412, 428 |
| `PATCH /jobs/{id}/lines/{lineId}` | If-Match (job's ETag) | 200 + job body + new ETag | 400, 404 job or line, 409 frozen, 412, 428 |
| `DELETE /jobs/{id}/lines/{lineId}` | If-Match (job's ETag) | 200 + job body + new ETag | 404 job or line, 409 frozen, 412, 428 |
| `POST /jobs/{id}/complete` | If-Match | 200 + new ETag | 404, 409 wrong state or no lines, 412, 428 |

All line writes return the whole job, because the client needs the new ETag and the recalculated total anyway. DELETE returns 200 with a body rather than 204 for the same reason.

Job response: `{ id, customerName, siteAddress, description, status, createdAt, updatedAt, completedAt, lines[{ id, type, description, quantity, unitPrice, total }], total, export: { status, attempts, lastError, externalInvoiceId, completedAt } | null }`.

**Problem types:** `validation-error` 400, `idempotency-key-missing` 400, `invalid-if-match` 400, `job-not-found` 404, `line-not-found` 404, `job-state-conflict` 409, `version-mismatch` 412, `idempotency-key-reused` 422, `precondition-required` 428.

**Write handler order**

1. `If-Match` missing or `*` → 428. Weak, list or non-integer tag → 400 (`invalid-if-match`). ETag format is strong `"7"`.
2. Load job with lines and export task → 404.
3. `job.Version != ifMatch` → 412 (checked before line lookup, so a stale client re-fetches instead of getting a misleading 404 for a line someone else deleted).
4. Domain method → 409, 404 `line-not-found` or 400 via `DomainExceptionHandler`.
5. `SaveChanges`; the UPDATE carries `WHERE version = @original`. `DbUpdateConcurrencyException` → 412.
6. Respond with the new ETag and body.

**Idempotency-Key flow (`POST /jobs`)**

1. Missing or longer than 100 chars → 400.
2. `request_hash` = SHA-256 of `JsonSerializer.SerializeToUtf8Bytes(boundDto)`.
3. Build the job, response body and ETag; add the job and the `idempotency_keys` row; one `SaveChanges` (one transaction). Commit → 201.
4. `DbUpdateException` whose inner `PostgresException` has `SqlState = 23505` and constraint `pk_idempotency_keys` → `ChangeTracker.Clear()`, read the stored row. Same hash → replay status, body, ETag and `Location: /jobs/{job_id}`. Different hash → 422.
5. A concurrent duplicate blocks on the PK until the first transaction ends: commit → step 4; rollback → its own insert succeeds. Only successful responses are stored.

---

## 8. Worker

**Options** (`ExportWorker` section, `ValidateOnStart`)

| Setting | Production | Tests |
| --- | --- | --- |
| BatchSize | 10 | 5 |
| LeaseDuration | 30 s | 500 ms |
| MaxAttempts | 5 | 50 |
| BackoffBase / BackoffMax | 1 s / 5 min | 10 ms / 200 ms |
| Jitter | 0–20 % | 0–20 % |
| PollInterval | 1 s | 50 ms |

Backoff: `min(BackoffBase × 2^attempts, BackoffMax) × (1 + random 0–0.2)`.

**Claim** (one statement; `Database.SqlQuery<ClaimedTask>` with interpolated parameters; `TimeSpan` maps to `interval`)

```sql
WITH next AS (
  SELECT id FROM export_tasks
  WHERE (status = 'Pending' AND next_attempt_at <= now())
     OR (status = 'Processing' AND lease_until < now())
  ORDER BY next_attempt_at
  LIMIT @batch
  FOR UPDATE SKIP LOCKED)
UPDATE export_tasks t
SET status = 'Processing', lease_token = @token, locked_by = @workerId,
    lease_until = now() + @leaseDuration, attempts = t.attempts + 1
FROM next WHERE t.id = next.id
RETURNING t.id AS "Id", t.job_id AS "JobId", t.attempts AS "Attempts", t.lease_token AS "LeaseToken";
```

**Loop** (`ExportProcessor.ProcessBatchAsync(workerId)`, called by `ExportWorker` and directly by tests)

1. Claim. Nothing claimed → delay `PollInterval`. Full batch → loop again immediately.
2. Process claimed tasks concurrently (`Parallel.ForEachAsync`, own DbContext each), inside a log scope `{TaskId, JobId, WorkerId, Attempt}`.
3. `Attempts > MaxAttempts` (crash loop) → dead-letter without calling accounting.
4. Load the job snapshot (`AsNoTracking`, with lines) and call `IAccountingClient.CreateInvoiceAsync(snapshot, idempotencyKey: task.Id)`.
5. **Success** → one transaction: fenced `ExecuteUpdateAsync` on `export_tasks` (`WHERE id = @id AND lease_token = @token AND status = 'Processing'`) to Succeeded with `external_invoice_id`; 0 rows → lease lost, warn, roll back. Otherwise load the job, `MarkExported`, `SaveChanges`, commit. Use `CancellationToken.None` for this commit so a successful call is not discarded by shutdown.
6. **Transient failure** → fenced update: status Pending, `next_attempt_at = now + backoff`, clear lease fields, `last_error`. If `Attempts >= MaxAttempts`, dead-letter instead.
7. **Permanent failure / dead-letter** → fenced update to DeadLettered with `last_error`, plus `MarkExportFailed` on the job, in one transaction.
8. Shutdown: stop claiming; in-flight tasks finish within the host shutdown timeout, or their leases expire.

`WorkerId` = `{MachineName}-{ProcessId}`. Logging: `AddJsonConsole(o => o.IncludeScopes = true)`.

**Fake accounting** (`FakeAccountingClient`, own `NpgsqlConnection` from `NpgsqlDataSource`, own transaction)

```sql
INSERT INTO fake_accounting.invoices (id, idempotency_key, job_id, total, created_at)
VALUES (@id, @key, @jobId, @total, now())
ON CONFLICT (idempotency_key) DO NOTHING
RETURNING id;
-- no row returned → SELECT id FROM fake_accounting.invoices WHERE idempotency_key = @key  (replay)
```

Options: `LatencyMin/Max`, `FailBeforeRate` (throw transient before the insert), `FailAfterRate` (insert, then throw transient: "response lost"), `PermanentFailureRate` (default 0). Uses `Random.Shared`. Exposes a thread-safe `ReplayCount` for tests.

---

## 9. Deduplication end to end

| Where a duplicate comes from | How it is stopped | Enforced by |
| --- | --- | --- |
| Client retries `POST /jobs` | Idempotency key replays the first response | `idempotency_keys` PK |
| `complete` called twice or concurrently | Version check (412) or state machine (409); one task per job | Unique `export_tasks.job_id` |
| Two workers hold the same task after lease expiry | Only the current token's write commits | `WHERE lease_token = @token` |
| Worker crashes after the external call, before commit | Task id is the idempotency key; receiver returns the existing invoice | Unique `fake_accounting.invoices.idempotency_key` |

Delivery to accounting is at-least-once; the effect is exactly-once. `export_tasks` is the outbox and the queue, so no relay is needed. **Known gap:** row four relies on the receiver honouring idempotency keys; otherwise look up by external reference (job id) first and run reconciliation for the remaining crash window.

---

## 10. Tests

Real PostgreSQL via Testcontainers (`postgres:17-alpine`), one container per run (`ICollectionFixture<PostgresFixture>`), migrations applied once, `TRUNCATE … CASCADE` of all five tables before each test. API tests use `WebApplicationFactory<Program>` with `UseSetting("ConnectionStrings:Jobs", …)`; `Program` exposes `public partial class Program;`.

| # | Test | Setup | Assertions |
| --- | --- | --- | --- |
| 1 | Exactly-once under contention | 200 jobs seeded via domain methods and completed; 4 concurrent `ProcessBatchAsync` loops with distinct worker ids; test options; fake FailBefore 10 %, FailAfter 10 %, latency 0–600 ms vs 500 ms lease; poll until nothing is Pending/Processing (60 s timeout) | 200 tasks Succeeded; 200 jobs Exported; exactly one invoice per `job_id`; each task's `external_invoice_id` matches its invoice; invoice total = job total; `ReplayCount > 0` |
| 2 | Stale lease is fenced | 1 completed job; worker A claims; SQL expires the lease; worker B claims (new token) and completes | A's complete and fail both return 0 rows; one invoice; task Succeeded, `locked_by` = B, attempts = 2 |
| 3 | Lost update prevented | Create job; two parallel PATCH with `If-Match: "1"` | Status codes {200, 412}; final ETag `"2"` (both race paths end in 412) |
| 4 | Idempotent create | Same key and body in parallel; then same key, different body | One job; identical 201 bodies and ETags; then 422 |
| 5 | Domain rules | `Job` unit tests | First line → InProgress; edit/remove line recalculates total; removing the last line blocks Complete; unknown line id → LineNotFound; add/edit/remove all frozen after Complete; each change bumps Version; total rounding |
| 6 | Header contract | PATCH without If-Match; POST without key; DELETE line on completed job; PATCH line with stale ETag | 428; 400; 409; 412 |

Tests 1–3 are the proof of the design: they cover exactly-once export, fencing and lost-update protection.

Cite test 1 in the README's AI section as the verification of AI-written worker code.

---

## 11. Docker compose

- `postgres`: `postgres:17-alpine`, `pg_isready` healthcheck, named volume.
- `migrate`: migrations bundle image (`src/Jobs.Core/Migrations.Dockerfile`), `command: ["--connection", ...]`, depends on postgres healthy, `restart: "no"`.
- `api`: API image, `8080:8080`, depends on migrate `service_completed_successfully`.
- `worker`: worker image, same dependency, fake accounting env (`FakeAccounting__FailBeforeRate=0.1`, `FailAfterRate=0.05`, latency 100–800 ms), run with `--scale worker=3`.
- Dev credentials inline in compose (`.gitignore` excludes `*.env`), passed as `ConnectionStrings__Jobs`.
- Multi-stage Dockerfiles (`sdk:10.0` build, `aspnet:10.0` / `runtime:10.0` final).

---

## 12. Build order

- [x] **0. Setup.** `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.slnx`, four projects, compose with postgres only, README skeleton, `docs/ai-log.md`.
- [x] **1. Domain + persistence + test 5.** Entities, state machine including `UpdateLine` / `RemoveLine`, EF configs (naming convention, `ValueGeneratedNever`, concurrency token, unique indexes, check constraints, precision), `InitialCreate` migration (`dotnet ef migrations add InitialCreate -p src/Jobs.Core -s src/Jobs.Api -o Persistence/Migrations`), domain unit tests. *(migration generated with `dotnet ef`, pinned as a local tool; `has-pending-model-changes` clean)*
- [x] **2. API.** All eight endpoints (line PATCH/DELETE share the write handler pattern), ETag helpers, ProblemDetails and exception handler, validation, OpenAPI + Swagger UI, `/health`, API Dockerfile, migrations bundle Dockerfile, compose `migrate` + `api`, `requests.http`.
- [x] **3. Idempotency.** Key row in the same transaction, replay with headers, 422.
- [x] **4. Test infrastructure + test 3.** Postgres fixture, API factory, lost-update test. *(passing)*
- [x] **5. Worker + fake accounting.** Options, claimer, processor, backoff, dead-letter, fake with fail-before/after, log scopes, worker Dockerfile, compose worker.
- [x] **6. Remaining tests.** Tests 1, 2, 4 and 6. *(passing)*
- [x] **7. README.**

---

## 13. README outline

1. **How to run:** `docker compose up --build --scale worker=3`; Swagger at `http://localhost:8080/swagger`; `requests.http` plus 4–5 curl examples (create with Idempotency-Key, get ETag, patch with If-Match, add line, complete, get export status); `dotnet test` (Docker must be running).
2. **Decisions and trade-offs:** domain choice; `Version` vs `xmin`; headers vs body; job as aggregate root (line edits conflict at job level, by design); DB queue vs RabbitMQ and why no relay; lease token vs distributed lock; attempts counted at claim; separate deployables and the migrate step (with the EF 9+ lock note); no CQRS/MediatR/repositories; the deduplication table.
3. **Known limitations:** idempotency keys never expire and are global (no tenant scope); no auth or multi-tenancy; single currency; fake accounting shares the DB; fixed lease (fencing keeps it correct, but work repeats); polling adds up to 1 s latency and constant load; exactly-once relies on the receiver honouring keys; `next_attempt_at` uses the app clock while leases use the DB clock (small skew tolerated); ExportFailed needs manual action (no replay yet); lines are hard-deleted with no history; two people editing different lines of one job conflict (412) even though their changes do not overlap; one API instance in compose.
4. **AI usage and verification:** running log from `docs/ai-log.md`, one line each: asked → returned → what was wrong → how verified. Include this review's catches (EF Guid key tracking, flaky test math, tautological dedup assertion, FluentAssertions licence) and anything AI got wrong, stated plainly. Claim SQL verified by test 1, not by reading.
5. **Next steps:** lease heartbeat → outbox relay to RabbitMQ + consumer inbox → auth + tenant id → OpenTelemetry (queue depth, export lag, dead-letter count) → dead-letter replay + ExportFailed alert → idempotency key TTL → `LISTEN/NOTIFY` → CI + Azure (Managed Identity, Key Vault) → keyset paging, line change history / soft delete, rate limiting, `If-None-Match`/304.

---

## 14. Interview talking points

| Likely question | Short answer |
| --- | --- |
| Why not exactly-once delivery? | Not achievable across a network boundary; at-least-once plus idempotent receivers gives an exactly-once effect |
| Lease token vs Redis lock? | A lock holder can pause past expiry and still act; the DB rejects a stale token's write, with no extra infrastructure |
| Is that a real fencing token? | It is a compare-and-set token checked by the store that holds the state; a classic fencing token is monotonic and checked by the downstream resource, which here is covered by the idempotency key |
| Why count attempts at claim? | A task that crashes its worker still uses up its budget and dead-letters instead of looping forever |
| Why does test 1 fail *after* writing the invoice? | It is the only way to exercise the idempotency key path; `ReplayCount > 0` proves it ran |
| 412 vs 409? | 412: your precondition failed, re-fetch. 409: state conflict, such as editing a completed job |
| Why does changing a line bump the job version? | The job is the aggregate root; total and completability depend on lines |
| Two people edit different lines: why does one get 412? | Deliberate coarse-grained locking: the total and the "has a line" rule span all lines. The loser re-fetches and retries. Per-line versions would allow it but cannot protect cross-line rules |
| Worker dies mid-call? | Lease expires, another worker reclaims, idempotency key prevents a second invoice |
| Accounting ignores idempotency keys? | Look up by external reference first, plus reconciliation for the crash window |
| Why not RabbitMQ? | The task table already is the outbox and queue; a broker adds a relay and consumer dedup without strengthening the guarantee |
| How does it scale? | SKIP LOCKED handles many workers; at volume move to a broker, partition by tenant, replace polling with LISTEN/NOTIFY |
| `Version` vs `xmin`? | Explicit, portable, survives dump/restore; `xmin` works too |
| Why a separate migrate step if EF 9+ locks? | Fail fast before app start, DDL in one place, SQL can be reviewed, the app runs as a least-privilege user, and a failed migration does not leave instances crash-looping |
| Another day? | Lease heartbeat and observability first |

Tie answers to experience: integration job lifecycles at Snoonu; RabbitMQ retries, idempotency and DLQ at Alta; Managed Identity and CI/CD at Leneo.

---

## 15. Verification

1. `dotnet build` — clean with warnings as errors.
2. `dotnet test` with Docker running — tests 1–6 green. Run test 1 a few times in a row to confirm it is stable.
3. `docker compose up --build --scale worker=3` — the migrations bundle exits 0; api and three workers start.
4. Run `requests.http` (or the curl examples): 201 with ETag `"1"` → PATCH with a stale ETag gives 412 → add two lines (status InProgress) → edit one, delete the other (total recalculates, ETag changes each time) → complete → DELETE line now gives 409 → GET until export status is Succeeded and job status is Exported.
5. Create ~50 jobs with a script, kill one worker container mid-run (`docker kill <worker container>`), then check in psql: `SELECT job_id, count(*) FROM fake_accounting.invoices GROUP BY job_id HAVING count(*) > 1` returns no rows, and the invoice count equals the number of completed jobs.
6. Worker logs show JSON lines carrying `TaskId`, `JobId`, `WorkerId`, `Attempt` scopes.
