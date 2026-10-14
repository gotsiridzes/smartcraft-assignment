# Craft Jobs Service: solution breakdown

How the service works, why it is built this way, and where its guarantees end. Read it top to bottom once, then use the section headings as a map when presenting.

---

## 1. The one-sentence version

A job (field work for a trade company) is created, filled with lines, and completed. A background worker then turns it into **exactly one invoice** in an external accounting system, even with many API instances, many workers, retries, crashes and a flaky accounting system.

Everything in the design serves that invariant: *every completed job produces exactly one invoice, never zero and never two.*

## 2. What can go wrong (the problems the design solves)

| # | Problem | Real-world cause | What would happen without protection |
| --- | --- | --- | --- |
| 1 | **Lost update** | Two office users edit the same job on two screens | The second save silently overwrites the first |
| 2 | **Duplicate job** | A field worker on bad mobile signal retries `POST /jobs` after a timeout | Two jobs, later two invoices |
| 3 | **Double export** | Two worker instances pick the same job | Two invoices |
| 4 | **Stale worker** | A worker pauses (GC, network stall) past its lease; another takes over; the first wakes up | Both write results; state corrupted, or two invoices |
| 5 | **Crash in the gap** | A worker crashes *after* accounting created the invoice but *before* we recorded it | A retry creates a second invoice |
| 6 | **Accounting down** | The external system is slow or unavailable | Exports lost, or a hot retry loop |

Each row is handled by a specific mechanism, and each mechanism is enforced by the **database**, so it holds even if application code has a bug.

---

## 3. Architecture

```
                 ┌────────────────────────┐
  clients ──────▶│  Jobs.Api  (n copies)  │──┐  job change + export task
  (office, app)  └────────────────────────┘  │  in ONE transaction
                                             ▼
                              ┌───────────────────────────────┐
                              │ PostgreSQL                    │
                              │  jobs, job_lines              │  system of record
                              │  idempotency_keys             │  API dedup
                              │  export_tasks                 │  outbox + work queue
                              │  fake_accounting.invoices     │  simulated external system
                              └───────────────────────────────┘
                                             ▲
                 ┌────────────────────────┐  │  claim: FOR UPDATE SKIP LOCKED
                 │ Jobs.Worker (n copies) │──┘  outcome: fenced by lease token
                 └───────────┬────────────┘
                             │ CreateInvoice(idempotency key = task id)
                             ▼
                    accounting system (fake: slow, fails on purpose)
```

**Projects**

| Project | Contents |
| --- | --- |
| `Jobs.Core` | Domain (`Job` aggregate, `JobLine`, `ExportTask`, state machine, validation), EF Core model, migrations |
| `Jobs.Api` | Minimal API endpoints, ETag / If-Match, Idempotency-Key, ProblemDetails |
| `Jobs.Worker` | `BackgroundService` loop, `TaskClaimer` (claim and fenced writes), `ExportProcessor`, fake accounting client |
| `Jobs.Tests` | Domain unit tests; Testcontainers integration tests for the API and the worker |

**Key architectural choices**

- **Two deployables, one database.** The API and the worker scale independently: `docker compose --scale worker=3`.
- **The database is both the system of record and the queue.** Completing a job and creating its export task happen in one transaction, so there is no "dual write" (saved the job but failed to publish the message). `export_tasks` *is* the outbox, and workers read it directly. So no relay process and no broker are needed.
- **No CQRS, MediatR or repository layer.** Business rules live on the `Job` entity, and endpoints use the `DbContext` directly. At this size, extra layers add ceremony, not safety.

---

## 4. Domain model and lifecycle

```
POST /jobs ─▶ Draft ──first line──▶ InProgress ──complete──▶ Completed ──worker──▶ Exported
               │                        │                        │               └──▶ ExportFailed
               └── edit header ─────────┴── add / edit /         └── frozen: any change = 409
                                            delete lines
```

- **`Job` is the aggregate root.** It owns its lines. Every change, to the header *or to any line*, goes through a `Job` method that validates state and **increments `Version`**.
- **Draft → InProgress** happens on the first added line ("work recorded = work started"). There is no separate start endpoint.
- **Complete** is allowed only from InProgress with at least one line. Deleting all lines keeps the job InProgress but blocks completion. Complete freezes the job and creates its `ExportTask` in the same unit of work.
- **Exported / ExportFailed** are set by the worker through `MarkExported` / `MarkExportFailed`, allowed only from Completed. They bump the version too.
- **Money:** `decimal` in C#, `numeric(18,2)` for prices and `numeric(18,3)` for quantities. Inputs with more decimals are rejected (400) instead of being silently rounded by the database. Each line total is rounded half away from zero; the job total is the sum of line totals.
- **Ids** are UUID v7, generated in the domain. They are time-ordered, so they index well. EF is told `ValueGeneratedNever()`; otherwise it would treat a new line with a pre-set id as an existing row and issue an UPDATE that matches nothing, which looks like a false conflict.

Source: [Job.cs](../src/Jobs.Core/Domain/Job.cs), [ExportTask.cs](../src/Jobs.Core/Domain/ExportTask.cs).

---

## 5. Data model: which constraint guarantees what

| Table | Purpose | Guarantee enforced by the database |
| --- | --- | --- |
| `jobs` | Job header, `status`, `version` | `version` is the optimistic concurrency token: every UPDATE carries `WHERE version = @original` |
| `job_lines` | Lines | Check constraints `quantity > 0` and `unit_price >= 0` |
| `idempotency_keys` | First response per Idempotency-Key | **Primary key on `key`**: a key can be stored once, so duplicates are serialised |
| `export_tasks` | Outbox + queue: status, attempts, lease, last error, invoice id | **Unique `job_id`**: one export per job, ever |
| `fake_accounting.invoices` | The simulated external system | **Unique `idempotency_key`**: the receiver never creates two invoices for one key |

Enums are stored as strings (readable, safe to reorder), timestamps as `timestamptz` in UTC, and table and column names are snake_case.

---

## 6. API layer

### 6.1 Endpoints

| Endpoint | Guard | Success |
| --- | --- | --- |
| `POST /jobs` | `Idempotency-Key` | 201 + ETag + Location |
| `GET /jobs/{id}` | — | 200 + ETag; body has lines, total, `version`, export status |
| `GET /jobs?status=` | — | 200, newest first, at most 50, each item has `version` |
| `PATCH /jobs/{id}` | `If-Match` | 200 + new ETag |
| `POST /jobs/{id}/lines` | `If-Match` | 200 + job + new ETag |
| `PATCH /jobs/{id}/lines/{lineId}` | `If-Match` | 200 + job + new ETag |
| `DELETE /jobs/{id}/lines/{lineId}` | `If-Match` | 200 + job + new ETag |
| `POST /jobs/{id}/complete` | `If-Match` | 200 + new ETag (export queued) |

Line writes return the whole job, because the client needs the new ETag and the recalculated total anyway.

### 6.2 Optimistic concurrency (lost-update protection)

**Idea:** no locks while a user is looking at the data. Instead, each write proves it is based on the latest version.

```
GET  /jobs/42                    ──▶  200, ETag: "7"
PATCH /jobs/42  If-Match: "7"    ──▶  200, ETag: "8"     (version 7 → 8)
PATCH /jobs/42  If-Match: "7"    ──▶  412, ETag: "8", currentVersion: 8   (someone else won)
PATCH /jobs/42  (no If-Match)    ──▶  428 Precondition Required
```

**Two layers, because a check alone is not enough:**

1. **Explicit check.** Load the job and compare `job.Version` with If-Match. A mismatch gives 412 straight away, before any domain logic, so a stale client never gets a misleading answer, such as a 404 for a line someone else deleted.
2. **Database check.** The UPDATE is `… WHERE id = @id AND version = @original`. If two requests both pass the check at the same moment (both read version 7), the second UPDATE matches 0 rows. EF raises `DbUpdateConcurrencyException`, which becomes 412. **This is what makes it correct under real concurrency;** the first check is an early exit.

**Details worth defending:**

- **Why headers, not a version field in the request?** ETag / If-Match is standard HTTP (RFC 9110), understood by clients, proxies and caches, and it keeps concurrency control out of the request body.
- **Why also `version` in the response body?** A list response has one set of headers, so it cannot carry one ETag per item. With `version` in the body, a client can edit straight from `GET /jobs`. The ETag stays the authority for a single job.
- **Why 412 and not 409?** 412 means "your precondition failed, re-fetch". 409 means "the state does not allow this", for example editing a completed job. Clients react differently to each.
- **Why 428 for a missing If-Match?** The alternative, "no header means last write wins", is exactly the lost update we are preventing.
- **412 tells the client what it missed:** it carries the current ETag and `currentVersion`, saving a round trip.
- **Why does a line change bump the job version?** The job is the aggregate root: its total and its "can be completed" rule depend on all lines. The trade-off is that two people editing *different* lines conflict, which is deliberate, coarse-grained locking. Per-line versions would allow it but could not protect cross-line rules.
- **Why an explicit `int Version` and not Postgres `xmin`?** It is explicit, portable, survives dump and restore, and doubles as the ETag. `xmin` would work too.

Source: [JobEndpoints.cs](../src/Jobs.Api/Endpoints/JobEndpoints.cs) (`MutateAsync`), [ETags.cs](../src/Jobs.Api/Http/ETags.cs).

### 6.3 API idempotency (duplicate-job protection)

**Idea:** the client sends a unique `Idempotency-Key` with each create. Retrying with the same key returns the *first* response instead of creating a second job.

```
POST /jobs  Idempotency-Key: K, body B    ──▶ 201, job 42             (stored: K → hash(B), response)
POST /jobs  Idempotency-Key: K, body B    ──▶ 201, same bytes, Idempotent-Replayed: true
POST /jobs  Idempotency-Key: K, body B'   ──▶ 422 (key reused for a different request)
POST /jobs  (no key)                      ──▶ 400
```

**How it works:**

1. Hash the request (SHA-256 of the *deserialised and re-serialised* body, so whitespace or key order do not make a retry look different).
2. Fast path: if the key exists, replay or 422.
3. Otherwise insert **the job and the key row in one transaction**. The key row stores the response status, body (as `text`, byte for byte) and ETag.
4. **Concurrent duplicates:** two requests with the same key both pass step 2. Both insert, but the second one *blocks on the key's primary key* until the first commits. It then gets a unique violation, rolls back (its job is never created), reads the stored row and replays it.

**Design decisions:**

- **No "in progress" state.** Many implementations insert a "processing" marker first and need timeouts for crashed requests. Because our key row commits *together with* the job, the primary key itself serialises duplicates, and a crash simply rolls back both.
- **`text`, not `jsonb`, for the stored response:** `jsonb` reorders keys, so a replay would not be byte-identical.
- **Only success is stored.** A 400 is not cached, so a fixed request can reuse the key.
- **Scope:** only `POST /jobs` needs a key. Every other write is already idempotent through If-Match: a retried PATCH with the old ETag gets 412 instead of applying twice.

Source: [Idempotency.cs](../src/Jobs.Api/Http/Idempotency.cs), `CreateJob` in [JobEndpoints.cs](../src/Jobs.Api/Endpoints/JobEndpoints.cs).

### 6.4 Request pipeline for every write

```
If-Match missing / *         → 428
If-Match malformed or weak   → 400 invalid-if-match
job not found                → 404 job-not-found
version ≠ If-Match           → 412 version-mismatch (+ current ETag, currentVersion)
domain rule broken           → 409 job-state-conflict | 404 line-not-found | 400 validation-error
SaveChanges: version changed in between          → 412 (race caught by WHERE version)
SaveChanges: export task for this job exists     → 412 (two completes raced; unique index caught it)
success                      → 200 + new ETag
```

### 6.5 Errors

All errors are RFC 9457 ProblemDetails with a stable `type`, so clients branch on the type rather than parsing text: `validation-error` (with per-field errors), `invalid-request` (body is not valid JSON), `idempotency-key-missing`, `idempotency-key-reused`, `invalid-if-match`, `precondition-required`, `version-mismatch`, `job-not-found`, `line-not-found`, `job-state-conflict`, `invalid-status`. Domain exceptions are mapped centrally in [DomainExceptionHandler.cs](../src/Jobs.Api/Http/DomainExceptionHandler.cs).

---

## 7. Completing a job: the transactional outbox moment

`POST /jobs/{id}/complete` runs one `SaveChanges`, which is one database transaction:

```
UPDATE jobs SET status='Completed', version=version+1, ... WHERE id=@id AND version=@v
INSERT INTO export_tasks (id, job_id, status='Pending', attempts=0, next_attempt_at=now, ...)
COMMIT
```

Either both happen or neither does. You cannot end up with a completed job that is never exported (zero invoices), or an export for a job that is not completed. This is the **transactional outbox** pattern; `export_tasks` is the outbox. With a message broker you would add a relay that copies outbox rows to the broker. Here the workers read the outbox directly.

Two concurrent completes: the version check rejects one (412), and as a last line of defence, the unique index on `export_tasks.job_id` makes a second task impossible.

---

## 8. The worker

### 8.1 The loop

```
loop:
  claimed = claim up to N due tasks            (one SQL statement)
  if none: sleep PollInterval (1 s); continue
  process all claimed tasks concurrently       (one lease covers one call)
  repeat immediately (there may be more work)
```

Defaults: batch 10, lease 30 s, max attempts 5, backoff `min(1 s × 2^attempts, 5 min) × (1 + random 0–20 %)`, poll 1 s.

Source: [ExportWorker.cs](../src/Jobs.Worker/ExportWorker.cs), [ExportProcessor.cs](../src/Jobs.Worker/ExportProcessor.cs).

### 8.2 Claiming: `FOR UPDATE SKIP LOCKED` + lease

```sql
WITH due AS (
    SELECT id FROM export_tasks
    WHERE (status = 'Pending'    AND next_attempt_at <= now())     -- due for (re)try
       OR (status = 'Processing' AND lease_until     <  now())     -- abandoned: lease expired
    ORDER BY next_attempt_at
    LIMIT $1
    FOR UPDATE SKIP LOCKED                                         -- skip rows another worker is claiming
)
UPDATE export_tasks AS t
SET status = 'Processing', lease_token = $2 /* new random token */, locked_by = $3,
    lease_until = now() + $4 /* 30 s */, attempts = t.attempts + 1
FROM due WHERE t.id = due.id
RETURNING t.id, t.job_id, t.attempts, t.lease_token;
```

What each part buys:

- **`FOR UPDATE SKIP LOCKED`:** while one worker is claiming rows, others *skip* them instead of waiting or taking them too. Many workers can pull from one table without blocking each other or double-claiming. If a row was claimed and committed a moment ago, Postgres re-checks it against the WHERE clause; it is no longer due, so it drops out.
- **One atomic statement:** select, lock and mark happen in a single statement (each statement is its own transaction). There is no window between "I picked it" and "I marked it", and the caller doesn't need to manage a transaction. Compare the common EF pattern `FromSql("SELECT … FOR UPDATE SKIP LOCKED")`: it only works when wrapped in an explicit transaction that also writes the claim. Without one, the lock is released straight after the SELECT, and two workers can take the same row.
- **Lease (`lease_until`):** the claim is exclusive for 30 s. A worker that dies simply disappears; the lease runs out and another worker reclaims the task. No locks are held during processing.
- **Lease token:** a new random token per claim. This is the *fencing token* (see 8.4).
- **Attempts counted at claim time:** a task whose worker crashes every time still runs out of attempts and gets dead-lettered, instead of being reclaimed forever (a "poison" task).
- **Database clock (`now()`):** lease and retry times all use one clock, so clock skew between worker machines doesn't matter.
- **Batch processed concurrently:** if 10 tasks were processed one after another, the last ones could outlive the 30 s lease and be taken over. In parallel, each lease covers one call.

Source: [TaskClaimer.cs](../src/Jobs.Worker/TaskClaimer.cs).

### 8.3 Calling accounting: at-least-once delivery, exactly-once effect

The worker loads a snapshot of the frozen job (customer, lines, total) and calls:

```
CreateInvoice(invoice, idempotencyKey: task.Id)
```

- **The same key on every attempt for this job.** Retries, takeovers by another worker and crash recovery all send the same key.
- **The receiver deduplicates:** the fake does `INSERT … ON CONFLICT (idempotency_key) DO NOTHING RETURNING id`, then selects the existing row. A repeated call returns *the same invoice* (`replayed = true`). `ON CONFLICT` makes this race-safe even when two workers call at the same instant.
- **This is the honest framing:** exactly-once *delivery* across a network is impossible, because you can never know whether a request that timed out was processed. So we accept **at-least-once delivery** (we may call more than once) and make the receiver **idempotent**. The **effect** is exactly once.
- **The fake fails on purpose:** latency, failures *before* writing (an outage) and failures *after* writing (lost response). The "after" case is the dangerous one, and it's how the tests prove the key does its job.

Source: [FakeAccountingClient.cs](../src/Jobs.Worker/Accounting/FakeAccountingClient.cs).

### 8.4 Recording the outcome: fencing

Every outcome write includes the fence:

```sql
WHERE id = @taskId AND lease_token = @myToken AND status = 'Processing'
```

- **Success**, in one transaction: the task becomes `Succeeded` with `external_invoice_id`, and the job becomes `Exported` (domain method plus version-checked UPDATE).
- **Transient failure:** the task goes back to `Pending` with `next_attempt_at = now() + backoff` and its lease cleared. When attempts are used up it is dead-lettered instead.
- **Permanent failure / attempts used up**, in one transaction: the task becomes `DeadLettered` with `last_error`, and the job `ExportFailed`.

If the lease expired and another worker re-claimed the task, the token no longer matches. The write updates **0 rows**, and the stale worker logs "Lease lost" and does nothing. This is **fencing**: the database rejects writes from a worker that no longer owns the task, however long it was paused.

> Terminology: a classic fencing token is a *monotonic* number checked by the protected resource. Ours is a compare-and-set token checked by the database that holds the state. The external side (accounting) is protected by the idempotency key instead.

### 8.5 Failure timelines

**A. Worker crashes after accounting created the invoice, before recording it**

```
W1 claims task (attempt 1, token A) ── CreateInvoice(key=T) ✔ invoice I ── 💥 crash
… 30 s, lease expires …
W2 claims task (attempt 2, token B) ── CreateInvoice(key=T) → returns I (replay) ── fence(B) ✔ Succeeded
Result: one invoice I.
```

**B. Stale worker (pause longer than the lease)**

```
W1 claims (token A) ── CreateInvoice … (stalls 40 s) ………………………… returns I ── fence(A) → 0 rows, "lease lost"
          … 30 s …  W2 claims (token B) ── CreateInvoice → I ── fence(B) ✔ Succeeded
Result: one invoice, W2's outcome stands, W1's write is ignored.
```

**C. Two workers poll at the same instant:** `SKIP LOCKED` hands each task to exactly one of them.

**D. Accounting down:** attempts 1→5 with backoff of about 2, 4, 8, 16 s, then `DeadLettered`, job `ExportFailed`, `last_error` visible in the API. No invoice. A person acts on it.

**E. Lost response every time:** the invoice is created on the first call, and every retry replays it. If retries run out, there is one invoice but the job shows ExportFailed. That is the known reconciliation gap (section 13).

### 8.6 Shutdown

On stop, the worker stops claiming. Tasks it already claimed run to the end (within the host's shutdown timeout), and their outcome is committed even during shutdown. If the process is killed, their leases expire and another worker picks them up.

---

## 9. Transactions and isolation

| Operation | Transaction boundary |
| --- | --- |
| Create job | One `SaveChanges`: job + idempotency key |
| Any job change | One `SaveChanges`, UPDATE with `WHERE version` |
| Complete | One `SaveChanges`: job + export task (outbox) |
| Claim | One statement (`UPDATE … FROM (SELECT … SKIP LOCKED) RETURNING`) |
| Success / dead-letter | Explicit transaction: fenced task update + job update |
| Retry | One statement, one row |
| **Accounting call** | **None, by design** |

**Why the external call is outside any transaction:**

1. A database transaction cannot include an external system (no two-phase commit with a SaaS API). If our commit fails after accounting created the invoice, rolling back does not remove the invoice. The crash window exists anyway, so idempotency is required either way.
2. Holding a lock and a connection for the length of a network call ties up the connection pool and leaves long transactions open.
3. The one thing it would give us, locks released automatically on crash, is already covered by lease expiry.

So: **short transactions inside the database, at-least-once delivery across the network, idempotency for the effect.**

**Why READ COMMITTED (the Postgres default) is enough:** no guard is "read, decide, then write". Every guard is a *conditional write* or a *constraint* that the database checks at write time: `WHERE version`, `WHERE lease_token`, the unique `job_id`, and the idempotency-key primary key. Stricter isolation levels would add retries without adding safety.

**No deadlocks:** every path that touches both tables locks the task row before the job row, and the API only touches a job before its task exists.

---

## 10. Who can conflict with whom

| Concurrent actors | Protection |
| --- | --- |
| Two clients editing a job | If-Match check + `WHERE version` → 412 |
| Two API instances, same job, same instant | `WHERE version` (one UPDATE matches 0 rows) → 412 |
| Client retries a create | Idempotency-Key + primary key → replay |
| Two concurrent creates, same key | Primary key blocks the second → replay |
| Two completes | Version check → 412; unique `export_tasks.job_id` as a backstop |
| Client edits while the worker exports | Impossible: the job is frozen (409); the worker's own update is version-checked |
| Two workers claim | `FOR UPDATE SKIP LOCKED` |
| Stale worker and current worker | Lease-token fence → 0 rows |
| Retried or duplicated accounting calls | Idempotency key = task id; unique key at the receiver |
| Worker crash loop | Attempts counted at claim → dead-letter |

---

## 11. Migrations, configuration, environments

- **Migrations are generated by EF Core tooling** (`dotnet ef migrations add`), with `dotnet-ef` pinned in `dotnet-tools.json` to the EF Core version. `has-pending-model-changes` confirms the model and the migrations agree.
- **They never run inside the application**, not even at startup in production. Reasons: the app's database user would need DDL rights; a failed migration would leave instances crash-looping in the middle of a deploy; and the SQL would never be reviewed. EF 9+ has a migration lock, so concurrent instances no longer race, but the other reasons still apply.
  - Local: `dotnet ef database update` (optionally `--connection`).
  - Compose / CI / production: an **EF Core migrations bundle**, a standalone executable built in its own image (`Migrations.Dockerfile`). It runs once before the API and workers start (`service_completed_successfully`).
  - Review: `dotnet ef migrations script --idempotent`.
  - Tests: Testcontainers database + `MigrateAsync()`.
- **Configuration:** one setting, `ConnectionStrings:Jobs`, read by the API, the worker and `dotnet ef`. Local secrets live in **user secrets**, shared by the API and the worker, outside the repository. Dev config files are excluded from Docker images.
- **Supply chain:** warnings are errors, and NuGet audit includes transitive packages, so a vulnerable dependency fails the build. A vulnerable SSH.NET pulled in by Testcontainers was found this way and fixed by upgrading.

---

## 12. Observability and testing

**Logs:** each export runs inside a scope carrying `TaskId`, `JobId`, `WorkerId` and `Attempt`, so one export can be followed across instances. Development uses readable single lines; everything else uses JSON lines for a log collector. **Health:** `/health` checks database connectivity.

**Tests** (real PostgreSQL through Testcontainers, because in-memory providers have no `SKIP LOCKED`, row locks or real constraints):

| Test | Proves |
| --- | --- |
| 1. Exactly-once under contention | 200 jobs and 4 competing workers. The fake fails 10 % before and 10 % after writing, and latency (up to 600 ms) exceeds the lease (500 ms), so takeovers really happen. Asserts exactly one invoice **per job** (not just unique keys, which the constraint already guarantees), matching totals, and that the replay path actually ran |
| 2. Stale lease is fenced | A worker whose lease expired cannot complete, fail or dead-letter; one invoice; the new holder's result stands |
| 3. Lost update | Two parallel PATCHes with the same ETag → one 200, one 412 carrying the current version |
| 4. Idempotent create | Parallel identical creates → one job, byte-identical responses; a different body → 422 |
| 5. Domain rules | State machine, frozen after complete, version bumps, rounding, validation |
| 6. HTTP contract | 428, 400s, 404s, 409s with the right ProblemDetails types |
| Extras | A task whose worker keeps crashing is dead-lettered; a permanent rejection dead-letters without retrying |

Test settings are fast (millisecond backoff, 50 attempts) so random failures never make the suite flaky. With production settings (5 attempts, 20 % failures), about 6 % of runs would dead-letter one of 200 tasks.

---

## 13. Trade-offs and limitations (say these before you're asked)

| Decision | Alternative | Why this one |
| --- | --- | --- |
| DB table as queue (`SKIP LOCKED`) | RabbitMQ / Service Bus | Atomic with the job change; no relay; one less system. A broker becomes worth it at high volume or with many consumers |
| Lease + fencing token | Distributed lock (Redis) | A lock holder can pause past expiry and still act; fencing makes the database reject it. No extra infrastructure |
| Fixed lease | Heartbeat | Exports are short calls. A heartbeat is next step 1 for long or variable work |
| Polling (1 s) | `LISTEN/NOTIFY` | Simple and robust; up to 1 s latency and a light constant load |
| Aggregate-level version | Per-line versions | Protects cross-line rules; costs false conflicts between edits to different lines |
| Idempotency without an "in progress" state | Status row + timeout | The primary key and the shared transaction do the work; less code, no stuck keys |
| Plain SQL for claim and retry | EF `FromSql` | `UPDATE … RETURNING` can't be wrapped in a subquery by EF; the retry time needs the database clock. Everything that touches the job goes through EF and the domain |

**Known limitations:**

- No authentication or multi-tenancy; idempotency keys are global and never expire.
- Accounting is a fake in the same database. A real client needs HTTP timeouts, a circuit breaker, error-code mapping, VAT and currency.
- **Reconciliation gap:** in the lost-response case, an invoice can exist while the job shows ExportFailed. A real system needs a reconciliation job, and must look the invoice up by job reference if the receiver ignores idempotency keys.
- A future "replay dead-letters" feature must *reuse the same task* (same idempotency key). A new task would mean a new key, and a possible second invoice.
- `idempotency_keys` and `export_tasks` grow forever (needs a time-to-live / archiving).
- No metrics or tracing; no alert on ExportFailed.

**Next steps, in order:** lease heartbeat → observability (queue depth, export lag, dead-letter count) → auth and tenants → dead-letter replay and alerting → key time-to-live → `LISTEN/NOTIFY` → broker and relay if volume demands → CI/CD and Azure (Managed Identity, Key Vault).

---

## 14. Likely questions, short answers

| Question | Answer |
| --- | --- |
| Why not exactly-once delivery? | Impossible across a network. At-least-once plus an idempotent receiver gives an exactly-once effect |
| Why no transaction around the export? | It can't include the external system, and the crash window exists anyway. Short transactions plus an idempotency key |
| What if a worker dies mid-call? | The lease expires, another worker reclaims the task with the same key, and the receiver returns the existing invoice |
| What if accounting ignores idempotency keys? | Look up by job reference before creating, and reconcile the remaining crash window |
| What if two workers grab the same task? | They can't at claim time (`SKIP LOCKED`). After a lease expiry only the current token's writes count |
| Why lease + token over a Redis lock? | A lock holder can pause past expiry and still act; the database rejects the stale write. No extra infrastructure |
| Why 412 vs 409 vs 428? | 412: stale version, re-fetch. 409: state forbids it. 428: you must send a version |
| How does a client get the version? | From any response's ETag (or `version` in the body); a 412 hands back the current one |
| Is the version check only in the API? | No, it's in the database UPDATE for every writer, the worker included |
| Why no RabbitMQ? | The table already is the outbox and the queue; a broker adds a relay and consumer dedup without strengthening the guarantee |
| How does it scale? | More workers (`SKIP LOCKED` scales well); then partition by tenant, `LISTEN/NOTIFY`, or a broker at high volume |
| Why migrations outside the app? | Least privilege, no crash-looping deploys, reviewable SQL |
| How did you use AI? | For review, scaffolding and code. Every claim was backed by tests or docs. It caught real bugs (EF key tracking, flaky test maths, a tautological assertion); I overruled it on migrations and a vulnerability exemption. See `docs/ai-log.md` |

---

## 15. Glossary

- **Idempotent:** doing it twice has the same effect as once.
- **Idempotency key:** a client-chosen id that lets the server recognise a retry of the same operation.
- **Optimistic concurrency:** no locks while reading; each write proves it's based on the latest version, or is rejected.
- **ETag / If-Match:** HTTP headers carrying a resource version, and a write's condition on it.
- **Aggregate root:** the entity that owns a group of objects and guards their rules (here `Job` owns its lines).
- **Transactional outbox:** write the business change and the "to-do" message in one transaction, then process the message separately.
- **`FOR UPDATE SKIP LOCKED`:** lock the selected rows and skip rows that others have locked, which lets many consumers share one queue table.
- **Lease:** time-limited ownership of a task that expires on its own if the owner dies.
- **Fencing token:** a token that must match on every write, so an owner whose lease expired can't write anymore.
- **At-least-once / exactly-once effect:** messages may be delivered more than once; idempotency makes the result happen once.
- **Dead letter:** a task that failed permanently or too often, parked for a person to handle.
- **Backoff with jitter:** waiting longer after each failure, with randomness so retries don't arrive all at once.
