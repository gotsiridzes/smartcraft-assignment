# Craft Jobs Service

A small .NET 10 + PostgreSQL service for trade companies (electricians, plumbers, carpenters). It turns finished field work into **exactly one invoice** in the customer's accounting system, even with many API and worker instances running at once.

1. The office creates a **job**: customer, site address, description.
2. Workers add, correct or remove **lines**: labour hours and materials, each quantity × unit price.
3. Someone **completes** the job. From then on it is frozen, because it is what gets invoiced.
4. A background worker **exports** the invoice to the accounting system, retries while that system is down, and records the result.

```
Draft ──first line──▶ InProgress ──complete──▶ Completed ──export──▶ Exported
                                                         └─retries spent─▶ ExportFailed
```

It guards against three things: lost updates (ETag / If-Match), duplicate jobs from retried requests (Idempotency-Key), and duplicate invoices from competing or crashing workers (`SKIP LOCKED` queue, lease token fencing, and an idempotency key on the accounting call).

## Status

| Area | State |
| --- | --- |
| API (8 endpoints), domain, persistence | Done |
| Background worker, fake accounting system | Done |
| EF Core migration `InitialCreate` | Generated with `dotnet ef`; `has-pending-model-changes` reports none. Applied outside the app (`dotnet ef database update` or a migrations bundle) |
| Build | Debug and Release: 0 warnings, 0 errors (warnings are errors, including NuGet audit) |
| Vulnerable packages | None (`dotnet list package --vulnerable --include-transitive`) |
| Tests (domain + Testcontainers integration) | Written, not run yet |
| CI, auth, observability | Not started (see next steps) |

## 1. How to run

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.100 or later; see `global.json`)
- Docker (Docker Desktop on Windows/macOS), needed for compose and for the integration tests
- Port 8080 free on the host (API). Compose publishes its own PostgreSQL on host port **5433**, so a locally installed PostgreSQL on 5432 is not affected

### Run everything in Docker

```bash
docker compose up --build --scale worker=3
```

This starts its own PostgreSQL container and runs the one-shot `migrate` service, an EF Core migrations bundle that applies the schema and exits. Then it starts the API on port 8080 and three workers. The containers talk to the database as `Host=postgres`, and the data lives in the `pgdata` volume, separate from any PostgreSQL on your machine. To inspect it from the host, connect to `localhost:5433` (user, password and database `craftjobs`). Stop everything with `docker compose down`; add `-v` to also delete the database volume.

### Examples

| What | Where |
| --- | --- |
| Swagger UI | http://localhost:8080/swagger |
| OpenAPI document | http://localhost:8080/openapi/v1.json |
| Health | http://localhost:8080/health |
| Full demo flow | [requests.http](requests.http) (Visual Studio, Rider, VS Code REST Client) |
| Postman collection | [postman/CraftJobs.postman_collection.json](postman/CraftJobs.postman_collection.json): happy path and error contract, with assertions; runs in the Collection Runner |

The ETag is the job version. It starts at `"1"` and goes up by one with every change. Clients never track versions themselves:

1. Read the ETag from any response (`GET`, or the previous write).
2. Send it back as `If-Match`.
3. On 412, the response carries the current ETag and `currentVersion`, so the client knows how far behind it is, re-fetches or merges, and retries.

Job bodies also include `version`, so a client working from `GET /jobs` can write without fetching each job first.

```bash
# Create (retrying with the same Idempotency-Key replays the first response)
curl -i -X POST localhost:8080/jobs -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-1' \
  -d '{"customerName":"Fjord Elektro AS","siteAddress":"Storgata 1, 0155 Oslo","description":"Rewire kitchen"}'

# Read: note the ETag header
curl -i localhost:8080/jobs/<id>

# Add a line with the current ETag (the job moves to InProgress, ETag becomes "2")
curl -i -X POST localhost:8080/jobs/<id>/lines -H 'Content-Type: application/json' -H 'If-Match: "1"' \
  -d '{"type":"Labour","description":"Electrician hours","quantity":2.5,"unitPrice":650}'

# Complete (freezes the job and queues the export)
curl -i -X POST localhost:8080/jobs/<id>/complete -H 'If-Match: "2"'

# Watch the export: status Completed -> Exported, export.status Pending -> Succeeded
curl -s localhost:8080/jobs/<id>
```

Check that no job was invoiced twice:

```bash
docker compose exec postgres psql -U craftjobs -d craftjobs -c "SELECT job_id, count(*) FROM fake_accounting.invoices GROUP BY job_id HAVING count(*) > 1;"
```

### Build and test

```bash
dotnet build
```

The tests need Docker running, because Testcontainers starts a throwaway PostgreSQL:

```bash
dotnet test
```

### Run against your own PostgreSQL

The API, the worker and `dotnet ef` all read the connection string `ConnectionStrings:Jobs` from the same configuration. The default in `appsettings.json` is `Host=localhost;Port=5432;Database=craftjobs;Username=craftjobs;Password=craftjobs`. Put your own connection string in **user secrets**: the API and the worker share one secrets store, it lives outside the repository, and it is loaded in Development.

```bash
dotnet user-secrets set "ConnectionStrings:Jobs" "Host=localhost;Port=5432;Database=craftjobs;Username=<user>;Password=<password>" --project src/Jobs.Api
```

Apply the schema with the EF Core tools. `dotnet ef` uses the API's configuration, including user secrets; `--connection` overrides it for a single run. Then start the API and as many workers as you like, each in its own terminal:

```bash
dotnet tool restore
```

```bash
dotnet ef database update --project src/Jobs.Core --startup-project src/Jobs.Api
```

```bash
dotnet ef database update --project src/Jobs.Core --startup-project src/Jobs.Api --connection "Host=localhost;Port=5432;Database=craftjobs;Username=<user>;Password=<password>"
```

```bash
dotnet run --project src/Jobs.Api
```

```bash
dotnet run --project src/Jobs.Worker
```

For extra workers, use `dotnet run --project src/Jobs.Worker --no-build`, so they don't rebuild while the first worker holds the files.

### Database migrations

The application never changes the schema: there is no migrate-at-startup and no migrate flag. Migrations are applied as a separate step:

| Where | How |
| --- | --- |
| Local development | `dotnet ef database update` (above), with `dotnet-ef` pinned in `dotnet-tools.json` to the EF Core version |
| Compose, CI, production | An EF Core **migrations bundle** ([Migrations.Dockerfile](src/Jobs.Core/Migrations.Dockerfile)): a standalone executable run once before the app starts, with `--connection` |
| Integration tests | The Testcontainers fixture calls `MigrateAsync()` on its throwaway database |

After changing the model, add a migration and check that the model and migrations agree:

```bash
dotnet ef migrations add <Name> --project src/Jobs.Core --startup-project src/Jobs.Api --output-dir Persistence/Migrations
```

```bash
dotnet ef migrations has-pending-model-changes --project src/Jobs.Core --startup-project src/Jobs.Api
```

To review the SQL before it runs, for example for a DBA or a release, generate an idempotent script:

```bash
dotnet ef migrations script --idempotent --project src/Jobs.Core --startup-project src/Jobs.Api --output migrate.sql
```

## 2. How it works

```
            ┌──────────────┐  job + export task      ┌──────────────────────────┐
 clients ──▶│ API (n)      │──── one transaction ───▶│ PostgreSQL               │
            └──────────────┘                         │  jobs, job_lines         │
            ┌──────────────┐  claim: SKIP LOCKED     │  export_tasks (queue)    │
            │ Worker (n)   │◀───────────────────────▶│  idempotency_keys        │
            └──────┬───────┘  fenced outcome writes  │  fake_accounting.invoices│
                   │ idempotency key = task id       └──────────────────────────┘
                   ▼
            fake accounting (own connection, fails and lags on purpose)
```

| Endpoint | Guard | Success | Errors |
| --- | --- | --- | --- |
| `POST /jobs` | `Idempotency-Key` | 201 + ETag + Location | 400, 422 key reused with another body |
| `GET /jobs/{id}` | | 200 + ETag (lines, total, export status) | 404 |
| `GET /jobs?status=` | | 200, newest first, at most 50 | 400 |
| `PATCH /jobs/{id}` | `If-Match` | 200 + new ETag | 400, 404, 409, 412, 428 |
| `POST /jobs/{id}/lines` | `If-Match` | 200 + job + new ETag | 400, 404, 409, 412, 428 |
| `PATCH /jobs/{id}/lines/{lineId}` | `If-Match` | 200 + job + new ETag | 400, 404, 409, 412, 428 |
| `DELETE /jobs/{id}/lines/{lineId}` | `If-Match` | 200 + job + new ETag | 404, 409, 412, 428 |
| `POST /jobs/{id}/complete` | `If-Match` | 200 + new ETag | 404, 409, 412, 428 |

Errors are ProblemDetails with stable `type` values ([ProblemTypes](src/Jobs.Api/Http/Problems.cs)), so a client can tell a 409 state conflict from a 412 version conflict.

**Worker loop** ([ExportProcessor](src/Jobs.Worker/ExportProcessor.cs), [TaskClaimer](src/Jobs.Worker/TaskClaimer.cs)):

1. Claim up to N due tasks in one statement: `FOR UPDATE SKIP LOCKED`, new lease token, `attempts + 1`.
2. Process the batch concurrently, so one lease covers one call.
3. Call accounting with the task id as the idempotency key.
4. Record the outcome with a write fenced by `id + lease_token + status = 'Processing'`:
   - Success: the task becomes Succeeded and the job Exported, in one transaction.
   - Transient failure: the task returns to Pending with exponential backoff and jitter.
   - Permanent failure or attempt limit: the task becomes DeadLettered and the job ExportFailed.
5. If the fenced write updates 0 rows, another worker took the task over. The worker logs it and moves on.

## 3. Key decisions and trade-offs

- **Domain.** A SmartCraft-style job-to-invoice flow, chosen because it makes concurrency and exactly-once matter.
- **Explicit `Version` column instead of Postgres `xmin`.** It is explicit, portable, survives dump and restore, and doubles as the ETag. `xmin` would also work.
- **ETag / If-Match carries the version, not a field in the request body.** These are standard HTTP and keep concurrency control out of the request. A missing header gives 428, a stale one 412 (with the current ETag and `currentVersion`), and a state conflict 409. Responses also carry a read-only `version`, because a list response can't put one ETag per item in its headers.
- **Version protection is enforced in the database, not only at the HTTP edge.** Every write to a job is an `UPDATE … WHERE version = @original` through EF Core, whether it comes from an API instance or from the worker (`MarkExported`). Two writers racing after the same read therefore can't both win. Export tasks use the lease token the same way.
- **The job is the aggregate root.** Adding, editing or deleting a line bumps `Job.Version`, because the total and the "needs a line to complete" rule span all lines. Two people editing different lines of the same job therefore conflict, by design.
- **Database as the queue (`SKIP LOCKED`) instead of RabbitMQ.** `export_tasks` is written in the same transaction as the completion, so it is the outbox and the queue at once and no relay is needed. A broker would add a relay and consumer dedup without strengthening the guarantee.
- **Lease + fencing token instead of a distributed lock.** A lock holder can pause past its expiry and still act. Here the database rejects the stale holder's write, with no extra infrastructure.
- **Attempts are counted at claim time.** A task that crashes its worker every time still runs out of attempts and is dead-lettered instead of looping forever.
- **Idempotency without an "in progress" state.** The key row is inserted in the job's transaction, so the primary key serialises concurrent duplicates. The loser replays the stored response.
- **Replays are byte-identical.** The response body is stored as `text`, not `jsonb`, because `jsonb` reorders keys. The request hash is taken over the bound DTO, so whitespace and key order in a retry do not count as a different request.
- **Separate API and worker deployables; migrations outside the app.** Schema changes run as their own step: `dotnet ef database update` locally, a migrations bundle in compose, CI and production. They never run at API startup, even in production. EF Core 9+ takes a migration lock, so concurrent instances no longer race, but startup migration still:
  - needs DDL rights for the app's database user;
  - couples a deploy to app start, so a failed migration leaves instances crash-looping;
  - runs SQL nobody reviewed.

  A separate step fails fast, can be reviewed (`migrations script --idempotent`), and lets the app run as a least-privilege user.
- **One clock for the queue.** Claims, leases and retry times use the database's `now()`.
- **No CQRS, MediatR or repositories.** The `DbContext` is used directly, and business rules live on the `Job` entity. At this size, extra layers add ceremony, not safety.
- **Validation lives in the domain.** Request DTOs are nullable, and `Job` reports field errors as a 400 with per-field messages. Money is `decimal` in C# and `numeric(18,2)` in the database (`numeric(18,3)` for quantities). Inputs with more decimal places are rejected instead of silently rounded, and line totals round half away from zero.

### Deduplication end to end

| Where a duplicate comes from | How it is stopped | Enforced by |
| --- | --- | --- |
| Client retries `POST /jobs` | Idempotency key replays the first response | `idempotency_keys` primary key |
| `complete` called twice or concurrently | Version check (412) or state machine (409); one task per job | Unique `export_tasks.job_id` |
| Two workers hold the same task after lease expiry | Only the current token's write commits | `WHERE lease_token = @token` |
| Worker crashes after the external call, before commit | Task id is the idempotency key; receiver returns the existing invoice | Unique `fake_accounting.invoices.idempotency_key` |

Delivery to accounting is at-least-once, and the effect is exactly-once.

## 4. Known limitations

- Idempotency keys never expire, and they are global (no tenant scope).
- No authentication or multi-tenancy. Single currency.
- The fake accounting system shares the database (own schema, own connection).
- The lease length is fixed. A call slower than the lease can be taken over; fencing keeps that correct, but the work is repeated.
- Polling adds up to 1 s latency and constant light database load.
- The exactly-once effect relies on the receiver honouring idempotency keys.
- A newly completed job's first attempt time comes from the API's clock; everything after that uses the database clock.
- ExportFailed needs manual action, because there is no replay endpoint yet.
- Lines are hard-deleted, with no history.
- Two people editing different lines of one job conflict (412) even though their changes do not overlap.
- Compose runs one API instance (fixed host port). The API is stateless and scales the same way the workers do.

## 5. How AI was used and verified

AI (Claude) was used to review the plan, to scaffold the solution, and to write most of the code. Every claim it made had to be backed by a test or by the library documentation. The running log is in [docs/ai-log.md](docs/ai-log.md). Highlights:

- **The plan review caught real defects.** These were the EF Core Guid key tracking pitfall (it would have turned every add-line into a false 412), attempts not counted for crashed workers, a flaky test design (20 % failures with 5 attempts across 200 tasks fails about 6 % of runs), and a dedup assertion that the unique constraint made tautological.
- **The claim SQL and fencing are verified by tests, not by reading.** Test 1 (`ExportTests.Every_completed_job_is_invoiced_exactly_once_under_contention`) runs 4 competing workers against a fake that fails before and after writing the invoice, with latency longer than the lease. It asserts exactly one invoice per job and that the replay path actually ran. Test 2 covers the stale lease directly.
- **Things AI got wrong or had to change** are listed in the log. They include:
  - a `JsonOptions` name clash;
  - the `jsonb` replay problem;
  - a hand-written first migration, replaced by one generated with `dotnet ef`;
  - a vulnerability exemption that hid a vulnerable SSH.NET pulled in by Testcontainers. Testcontainers was upgraded to 4.16 and the exemption removed.

## 6. Next steps (in priority order)

1. Lease heartbeat for long external calls.
2. Outbox relay to RabbitMQ, with an inbox table on consumers.
3. Auth (JWT) and multi-tenancy, with a tenant id on every row.
4. OpenTelemetry: queue depth, export lag, dead-letter count, and traces across API and worker.
5. A dead-letter replay endpoint and an alert on ExportFailed.
6. TTL cleanup for idempotency keys.
7. `LISTEN/NOTIFY` to replace polling.
8. CI (GitHub Actions) and Azure deployment with Managed Identity and Key Vault.
9. Keyset paging, line change history or soft delete, rate limiting, and `If-None-Match`/304.

## Project layout

```
src/Jobs.Core     Domain (Job aggregate, state machine), EF Core model, migrations
src/Jobs.Api      Minimal API endpoints, ETag / Idempotency-Key handling, ProblemDetails
src/Jobs.Worker   Export loop, task claimer (SKIP LOCKED + fencing), fake accounting
tests/Jobs.Tests  Domain unit tests; Testcontainers integration tests for API and worker
docs/             Implementation plans and the AI usage log
```
