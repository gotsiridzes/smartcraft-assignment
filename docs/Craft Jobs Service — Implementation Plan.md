# Craft Jobs Service — Implementation Plan

Oct 14, 2026 · @Saba

## Assignment and how this plan meets it

Build a small, production-minded .NET service in about 3.5 hours. It proves one thing well: every completed job produces exactly one invoice, even with many API and worker instances running at once.

| Requirement from SmartCraft | How this plan meets it |
| --- | --- |
| API endpoints to create, retrieve and update data | `POST /jobs`, `POST /jobs/{id}/lines`, `GET /jobs/{id}`, `GET /jobs?status=`, `PATCH /jobs/{id}`, `POST /jobs/{id}/complete` |
| Persist data in a database | PostgreSQL via EF Core with migrations |
| Use AI tools in development | AI used for design, scaffolding and review; every claim verified by tests or docs, logged in the README |
| Architecture, libraries, tooling are yours | Separate API and worker deployables, database-backed work queue, Testcontainers integration tests |
| Keep scope manageable, prioritise | Hard priority on concurrency and exactly-once; explicit cut list and next steps |
| README: run, decisions and trade-offs, limitations, AI usage | Outline in the README section below |
| Be ready to walk through and discuss improvements | Talking points at the end |

Time budget: about 3.5 hours of build, leaving 30 minutes of slack for things going wrong.

## Business overview

The service turns finished field work into exactly one invoice in the customer's accounting system. It is built for small trade companies such as electricians, plumbers and carpenters, which are SmartCraft's core customers.

**The problem.** Work done on site has to be re-entered as an invoice in accounting. A missed invoice is lost revenue. A duplicate invoice bills a customer twice and damages trust.

**What the service does.**

1. The office or foreman creates a **job**: customer, site address, description.
2. Workers add **lines**: labour hours and materials, each quantity × unit price.
3. Someone **completes** the job. From then on it is frozen, because it is what gets invoiced.
4. A background worker **exports the invoice** to the accounting system, retries if it is down, and records the result.

**Actors.** Office staff editing jobs, sometimes on two screens at once. Field workers on phones with poor signal, whose requests get retried. The external accounting system, which is slow or down at times. Several worker instances competing for exports.

**Business rules and what protects them.**

| Rule | Protected by |
| --- | --- |
| Every completed job produces exactly one invoice, never zero and never two | Unique export task per job, lease + fencing token, idempotency key on the external call |
| Two people editing the same job never silently overwrite each other | `Version` column, ETag / If-Match, 412 |
| A retried request never creates a duplicate job | Idempotency-Key header |
| Lines change only while the job is Draft or InProgress | Domain state machine |
| A job needs at least one line before it can be completed | Domain validation |
| An accounting outage never loses an export | Retry with backoff, dead-letter, visible export status |

**Lifecycle.** Draft → InProgress → Completed → Exported, or ExportFailed after the retry budget is spent.

**How the service behaves when things go wrong.**

- Accounting down: retry with exponential backoff and jitter. After the attempt limit, the job shows ExportFailed for a person to act on.
- Worker crashes mid-export: its lease expires and another worker takes over. The idempotency key stops a second invoice.
- Two workers hold the same task: only the current lease holder's completion commits.

**Guiding priority.** "Exactly one invoice per completed job" is the invariant everything serves. Anything that does not support it, or the concurrency story, goes to next steps.

## Scope and priorities

The build covers the concurrency and exactly-once story end to end; breadth features are deliberately left out. Everything outside "In scope" is either simplified or listed as a next step in the README.

**In scope (the story)**

- Jobs with lines; lines can be added but not edited or deleted
- State machine: Draft → InProgress → Completed → Exported / ExportFailed
- `POST /jobs` with Idempotency-Key
- `GET /jobs/{id}` with ETag, export status included in the response
- `PATCH /jobs/{id}`, `POST /jobs/{id}/lines`, `POST /jobs/{id}/complete`, all with mandatory If-Match
- Worker: claim with `SKIP LOCKED`, lease, fencing token, retry with backoff, dead-letter
- Fake accounting system that honours idempotency keys and fails at random
- Integration tests for exactly-once, stale lease and lost update; domain unit tests
- docker-compose with `--scale worker=3`; README

**Simplified**

- `GET /jobs?status=` returns a fixed limit, no keyset paging
- Idempotency has no "in progress" state: the key row is inserted in the same transaction as the job, so a concurrent duplicate waits on the unique index, then replays the stored response
- Fake accounting lives in the same database, in its own schema
- Single currency per service

**Out of scope (next steps)**

- RabbitMQ with an outbox relay and consumer inbox
- Auth and multi-tenancy
- Keyset paging, editing and deleting lines
- GitHub Actions CI (stretch goal if time allows)
- OpenTelemetry, idempotency key cleanup, dead-letter replay, lease heartbeat

**Decisions already made**

| Topic | Decision | Alternative named in the README |
| --- | --- | --- |
| Concurrency token | Explicit `int Version`, incremented in domain methods | Postgres `xmin` mapped with `IsRowVersion()` |
| Version scope | Job is the aggregate root: adding a line bumps `Job.Version` | Per-line versions |
| Version transport | ETag / If-Match headers; 428 if missing, 412 if stale | Version field in the JSON body |
| Database | PostgreSQL | SQL Server with `UPDLOCK, READPAST, ROWLOCK` and `rowversion` |
| Queue | Database table claimed with `FOR UPDATE SKIP LOCKED` | RabbitMQ |
| Worker coordination | Lease + fencing token | Distributed lock (Redis) |
| Migrations | One-shot `migrate` service in compose | Migrate at API startup (races with several instances) |

## Architecture and solution structure

Two deployables, the API and the worker, share one domain project and one PostgreSQL database. The database is both the system of record and the work queue, so a job's state change and its export task commit in one transaction.

&#91;embedded content: service architecture · 2 deployables, 1 database\]

API instances write the job and its export task in one transaction; workers claim export tasks with SKIP LOCKED and call accounting with the task id as the idempotency key.

**Stack**

- .NET 10, ASP.NET Core minimal APIs
- EF Core with Npgsql, migrations bundled for the `migrate` service
- `Microsoft.Extensions.Hosting` `BackgroundService` for the worker
- xUnit, Testcontainers for PostgreSQL, FluentAssertions or Shouldly
- docker-compose: `postgres`, `migrate`, `api`, `worker` (scaled to 3)

**Solution structure**

```
src/Jobs.Core
  Domain/        Job, JobLine, JobStatus, ExportTask, domain errors
  Persistence/   JobsDbContext, entity configs, migrations
src/Jobs.Api
  Endpoints/     JobEndpoints (one file per feature is fine)
  Http/          ETag helpers, IdempotencyFilter, ProblemDetails mapping
src/Jobs.Worker
  ExportWorker   BackgroundService loop
  TaskClaimer    claim / complete / fail SQL
  FakeAccounting IAccountingClient + fake implementation
tests/Jobs.Tests
  Integration/   Testcontainers fixture, API and worker tests
  Domain/        state machine unit tests
docker-compose.yml
README.md
```

**Design rules**

- No CQRS, MediatR or repository layer. EF Core `DbContext` is used directly; at this size extra layers add ceremony, not safety.
- Business rules live on the `Job` entity: `AddLine`, `Update`, `Complete` each validate state and increment `Version`.
- `Complete` creates the `ExportTask` in the same `SaveChanges`, so there is no dual write.
- Raw SQL only where EF cannot express it: the claim query with `SKIP LOCKED` and the fenced completion.
- Errors are ProblemDetails with stable `type` values, so clients can tell a 409 state conflict from a 412 version conflict.

## Data model

Five tables; three unique constraints carry the business guarantees, so they hold even if application code has a bug.

| Table | Columns | Key constraints |
| --- | --- | --- |
| `jobs` | id (uuid), customer\_name, site\_address, description, status, version (int), created\_at, updated\_at, completed\_at | PK id; `version` is the EF concurrency token |
| `job_lines` | id, job\_id, type (Labour / Material), description, quantity (numeric), unit\_price (numeric) | FK job\_id; quantity > 0, unit\_price ≥ 0 |
| `export_tasks` | id, job\_id, status (Pending / Processing / Succeeded / DeadLettered), attempts, next\_attempt\_at, lease\_token (uuid null), locked\_by, lease\_until, last\_error, external\_invoice\_id, created\_at, completed\_at | **Unique job\_id**: one export per job; index on (status, next\_attempt\_at) |
| `idempotency_keys` | key, request\_hash, response\_status, response\_body (jsonb), job\_id, created\_at | **PK key** |
| `fake_accounting.invoices` | id, idempotency\_key, job\_id, total, created\_at | **Unique idempotency\_key**: the simulated external system's dedup |

Notes:

- Money is `decimal` in C# and `numeric(18,2)` in the database, never `double`.
- Timestamps are `timestamptz`, written as UTC.
- Enums are stored as strings for readable data and safe reordering.
- `jobs.status` mirrors the export outcome (Exported / ExportFailed) so `GET /jobs/{id}` stays a single read plus one join.

## API, concurrency and idempotency

Every write to an existing job requires `If-Match`, and job creation requires `Idempotency-Key`. Together these stop lost updates and duplicate jobs.

| Endpoint | Guard | Success | Errors |
| --- | --- | --- | --- |
| `POST /jobs` | Idempotency-Key required | 201 + ETag + Location | 400 validation, 422 key reused with a different body |
| `GET /jobs/{id}` | none | 200 + ETag; body includes lines, total, export status | 404 |
| `GET /jobs?status=` | none | 200, newest first, fixed limit 50 | 400 |
| `PATCH /jobs/{id}` | If-Match | 200 + new ETag | 404, 409 frozen, 412 stale, 428 missing |
| `POST /jobs/{id}/lines` | If-Match; bumps job version | 201 + new job ETag | 404, 409 frozen, 412, 428 |
| `POST /jobs/{id}/complete` | If-Match; queues export in same transaction | 200 + new ETag | 404, 409 invalid state or no lines, 412, 428 |

**ETag / If-Match flow**

1. `GET` returns `ETag: "7"`, where 7 is `jobs.version`.
2. The client sends `If-Match: "7"` with its write.
3. Missing header → 428. Header ≠ current version → 412.
4. The UPDATE also carries `WHERE version = 7`. A race between read and write throws `DbUpdateConcurrencyException`, which also maps to 412.
5. Success increments `version` and returns the new ETag.

**Idempotency-Key flow (`POST /jobs`)**

1. Hash the request body (SHA-256).
2. In one transaction: insert the job and the `idempotency_keys` row with the response.
3. On commit, return 201.
4. On unique violation of the key: read the stored row. Same hash → replay the stored status and body. Different hash → 422.
5. A concurrent duplicate blocks on the key's unique index until the first transaction commits, then follows step 4. No "in progress" state is needed.

**Why headers, not a version field in the body:** HTTP standardises them, they keep concurrency control out of the resource, and clients, proxies and caches already understand them.

## Background worker

Any number of worker instances can run; `SKIP LOCKED` stops them claiming the same task, and the fencing token stops a stale worker from completing one it no longer owns.

**Claim (one statement, batch of N)**

```sql
UPDATE export_tasks
SET status = 'Processing', lease_token = @token, locked_by = @worker,
    lease_until = now() + @leaseDuration
WHERE id IN (
  SELECT id FROM export_tasks
  WHERE (status = 'Pending' AND next_attempt_at <= now())
     OR (status = 'Processing' AND lease_until < now())
  ORDER BY next_attempt_at
  FOR UPDATE SKIP LOCKED
  LIMIT @batch)
RETURNING *;
```

**Loop**

1. Claim a batch. Nothing claimed → wait the poll interval (for example 1 s) and repeat.
2. For each task, call `IAccountingClient.CreateInvoice(jobSnapshot, idempotencyKey: task.Id)`.
3. Success → in one transaction: `UPDATE export_tasks SET status='Succeeded', external_invoice_id=@id WHERE id=@id AND lease_token=@token`, and set the job to Exported. Zero rows updated → the lease was lost; log a warning and roll back.
4. Transient failure (timeout, 5xx) → `attempts + 1`, `next_attempt_at = now() + backoff`, clear the lease, status back to Pending. Fenced by `lease_token` too.
5. Permanent failure (4xx) or `attempts ≥ max` → status DeadLettered, job ExportFailed, `last_error` stored.
6. On shutdown, stop claiming; in-flight tasks finish or their leases expire.

**Settings** (in `appsettings.json`): batch size 10, lease 30 s, max attempts 5, backoff `min(2^attempts s, 5 min)` plus up to 20% jitter, poll interval 1 s.

**Fake accounting client.** Writes to `fake_accounting.invoices`. An existing idempotency key returns the original invoice instead of creating one. Configurable failure rate and latency simulate a real flaky system.

**Logging.** Every log line carries a scope with `TaskId`, `JobId`, `WorkerId` and `Attempt`, so one export can be traced across instances.

## Deduplication end to end

Duplicates can enter at four points, and each one is stopped at the layer where it appears. Name this table in the README so the deduplication reads as deliberate.

| Where a duplicate comes from | How it is stopped | Enforced by |
| --- | --- | --- |
| Client retries `POST /jobs` after a timeout | Idempotency key replays the first response | `idempotency_keys` primary key |
| `complete` called twice, or concurrently | State machine rejects the second; one export task per job | Unique `export_tasks.job_id` |
| Two workers process the same task after a lease expiry | Only the current lease holder's completion commits | `WHERE lease_token = @token` (fencing) |
| Worker crashes after the external call, before commit, and the task is retried | Task id sent as the idempotency key; receiver returns the existing invoice | Unique `fake_accounting.invoices.idempotency_key` (inbox pattern at the receiver) |

Delivery to accounting is at-least-once; the effect is exactly-once.

**No outbox relay.** `export_tasks` is the outbox and the queue at once: it is written in the job's transaction and read directly by the worker. A relay only exists to move rows to a broker. Adding RabbitMQ later means adding a relay plus an inbox table on the consumer; the rest of the design stays.

**Known gap.** Row four depends on the accounting system honouring idempotency keys. If a real one does not, look up the invoice by external reference (job id) before creating it. A small crash window remains, which a reconciliation job closes. List this under known limitations.

## Tests

Tests 1 to 3 are the proof of the design and are never cut. They run against a real PostgreSQL through Testcontainers, because in-memory providers do not support `SKIP LOCKED` or real locking.

| # | Test | Setup | Assertion | Priority |
| --- | --- | --- | --- | --- |
| 1 | Exactly-once under contention | 200 completed jobs, 4 workers running concurrently, fake accounting failing 20% of calls | 200 invoices, no duplicate keys, all tasks Succeeded | Must |
| 2 | Stale lease is fenced | Worker A claims, lease expires, worker B claims and completes | A's completion updates 0 rows; one invoice | Must |
| 3 | Lost update prevented | Two parallel `PATCH` with the same ETag | One 200, one 412; version incremented once | Must |
| 4 | Idempotent create | Same key and body sent twice in parallel | One job; identical responses | Stretch |
| 5 | Domain rules | Unit tests on `Job` | Frozen after Complete; Complete needs a line; each change bumps Version | Must, cheap |
| 6 | Header contract | `PATCH` without If-Match; same key with different body | 428; 422 | Nice |

Tips:

- Use one container per test class (`IClassFixture`) and truncate tables between tests, to keep the suite fast.
- Test 2 is easiest with a 1 s lease and calling `TaskClaimer` directly, not the full `BackgroundService`.
- Test 1 is the verification for AI-written worker code: cite it in the README's AI section.

## Build order and time boxes

The build is 3 h 30 min in seven steps, each ending in something that runs. Commit at the end of every step so the git history tells the story.

- [ ] **0. Setup, 15 min.** Solution and four projects, docker-compose with PostgreSQL, README skeleton, start the AI log.
- [ ] **1. Domain and persistence, 35 min.** `Job`, `JobLine`, `ExportTask`, state machine and `Version`; EF configs and unique indexes; first migration; `migrate` service in compose.
- [ ] **2. Job endpoints with ETag / If-Match, 35 min.** Create (without idempotency yet), get, list, patch, add line, complete; ProblemDetails; 412 and 428 handling.
- [ ] **3. Idempotency key on `POST /jobs`, 20 min.** Key row in the same transaction, replay and 422.
- [ ] **4. Worker and fake accounting, 45 min.** Claim SQL, fenced complete and fail, backoff, dead-letter, graceful shutdown, log scopes.
- [ ] **5. Tests, 40 min.** Testcontainers fixture, tests 1, 2, 3 and domain tests.
- [ ] **6. README, 20 min.** Run instructions, decisions, limitations, AI usage, next steps.

**Cut order if behind schedule** (first cut first):

1. Test 4 and test 6
2. `GET /jobs?status=` list endpoint (keep get by id)
3. Log scope polish
4. Jitter on backoff (keep plain exponential)

Never cut tests 1 to 3, the fencing token or the README. An unfinished feature with an honest README beats a finished one without proof.

## README outline

The README follows the four headings SmartCraft asked for, plus next steps; keep it short and link into the code.

**1. How to run**

- `docker compose up --build --scale worker=3`
- Swagger / OpenAPI URL, and 4 to 5 curl examples: create with Idempotency-Key, get with ETag, patch with If-Match, complete, get again to see export status
- `dotnet test` (Docker must be running for Testcontainers)

**2. Key decisions and trade-offs**

- Domain choice: a SmartCraft-style job-to-invoice flow, chosen because it forces concurrency and exactly-once to matter
- Explicit `Version` column vs Postgres `xmin`
- ETag / If-Match vs version in the body; job as aggregate root
- Database as the queue (`SKIP LOCKED`) vs RabbitMQ, and why there is no outbox relay
- Lease + fencing token vs a distributed lock
- Separate API and worker deployables; one-shot migration service
- No CQRS / MediatR / repositories at this size
- The deduplication table from this plan

**3. Known limitations**

- Idempotency keys never expire
- No authentication or multi-tenancy
- Single currency
- Fake accounting shares the database
- Fixed lease length: an external call slower than the lease can be taken over (fencing keeps it correct, but work is repeated)
- Polling adds up to 1 s latency and constant database load
- The exactly-once effect relies on the receiver honouring idempotency keys

**4. How AI was used and verified**

Keep a running log while building, one line each: what you asked, what came back, what was wrong or changed, how you verified it. Examples to include:

- Design discussion: AI proposed `xmin` for concurrency; you questioned it, checked the Npgsql docs, and chose an explicit column for clarity
- Scaffolding endpoints and EF configs: reviewed line by line
- Claim SQL: verified by test 1 under contention, not by reading it
- Anything AI got wrong, stated plainly

**5. Next steps, in priority order**

1. Lease heartbeat for long external calls
2. Outbox relay to RabbitMQ with an inbox table on consumers
3. Auth (Auth0 / JWT) and multi-tenancy with a tenant id on every row
4. OpenTelemetry: queue depth, export lag, dead-letter count, traces across API and worker
5. Dead-letter replay endpoint and an alert on ExportFailed
6. TTL cleanup for idempotency keys
7. `LISTEN/NOTIFY` to replace polling
8. GitHub Actions CI; Azure deployment with Managed Identity and Key Vault
9. Keyset paging, line editing, rate limiting

## Interview talking points

Expect questions to probe where each guarantee ends; have a one-line answer ready for each.

| Likely question | Short answer |
| --- | --- |
| Why not exactly-once delivery? | It is not achievable across a network boundary; at-least-once delivery plus idempotent receivers gives an exactly-once effect |
| Why a lease and fencing token instead of a Redis lock? | A lock holder can pause past its expiry and still act; fencing makes the database reject the stale write, and it needs no extra infrastructure |
| Why 412 and not 409 for a stale version? | 412 means the client's precondition failed, so re-fetch; 409 is reserved for state conflicts like editing a completed job |
| Why does adding a line bump the job's version? | The job is the aggregate root; its total and completability depend on its lines, so any change to them is a change to the job |
| What happens if a worker dies mid-call? | The lease expires, another worker reclaims the task, and the idempotency key stops a second invoice |
| What if the accounting system ignores idempotency keys? | Look up by external reference before creating, and run reconciliation for the remaining crash window |
| Why not RabbitMQ? | The task table already is the outbox and queue; a broker adds a relay and consumer dedup without improving this guarantee. It is next step 2 |
| How does it scale? | `SKIP LOCKED` scales to many workers; at high volume, move to a broker, partition by tenant, and replace polling with `LISTEN/NOTIFY` |
| Why `Version` instead of `xmin`? | Explicit, portable, survives dump and restore; `xmin` is the native alternative and works too |
| What would you do with another day? | The next-steps list, starting with lease heartbeat and observability |

Tie answers back to your experience: integration job lifecycles at Snoonu, RabbitMQ with retries, idempotency and DLQ at Alta, and Managed Identity and CI/CD at Leneo.
