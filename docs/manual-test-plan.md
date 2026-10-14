# Manual test plan

Walks the whole workflow against your own PostgreSQL. Each step lists the request (numbered as in [requests.http](../requests.http)), the expected response, and what to check in the database.

## 0. Setup (once)

Store your connection string in user secrets. The API, the worker and `dotnet ef` all read it, and it stays outside the repository:

```bash
dotnet user-secrets set "ConnectionStrings:Jobs" "Host=localhost;Port=5432;Database=craftjobs;Username=<user>;Password=<password>" --project src/Jobs.Api
```

Restore the pinned EF tool and create the schema. The database is created if it does not exist, provided the user has `CREATEDB`:

```bash
dotnet tool restore
```

```bash
dotnet ef database update --project src/Jobs.Core --startup-project src/Jobs.Api
```

To target a database without touching user secrets, pass the connection string explicitly:

```bash
dotnet ef database update --project src/Jobs.Core --startup-project src/Jobs.Api --connection "Host=localhost;Port=5432;Database=craftjobs;Username=<user>;Password=<password>"
```

**DB check:** one migration applied, and five tables in two schemas.

```sql
SELECT "MigrationId" FROM "__EFMigrationsHistory";
-- 20261014115123_InitialCreate

SELECT table_schema, table_name
FROM information_schema.tables
WHERE table_schema IN ('public', 'fake_accounting')
ORDER BY 1, 2;
-- fake_accounting.invoices, public.__EFMigrationsHistory, export_tasks, idempotency_keys, job_lines, jobs
```

Start **only the API** for now, so the export task can be seen waiting before a worker picks it up:

```bash
dotnet run --project src/Jobs.Api
```

Swagger: http://localhost:8080/swagger. Health: http://localhost:8080/health should return `Healthy`, which also proves the database connection works.

Keep this query open; it shows the whole state in one view:

```sql
SELECT j.id, j.status, j.version, j.updated_at,
       t.status AS export_status, t.attempts, t.locked_by, t.lease_until,
       t.next_attempt_at, t.last_error, t.external_invoice_id
FROM jobs j
LEFT JOIN export_tasks t ON t.job_id = j.id
ORDER BY j.created_at DESC;
```

## 1. Happy path

| # | Request | Expect | DB check |
| --- | --- | --- | --- |
| 1 | Create job, `Idempotency-Key: demo-job-0001` | 201, `ETag: "1"`, `Location: /jobs/{id}`, status `Draft` | `jobs`: 1 row, `Draft`, version 1. `idempotency_keys`: 1 row, `response_status` 201, `job_id` = new id |
| 2 | Same request again | 201, same body byte for byte, header `Idempotent-Replayed: true` | Still 1 job and 1 key: no duplicate |
| 3 | `GET /jobs/{id}` | 200, `ETag: "1"`, `lines: []`, `total: 0`, `export: null` | — |
| 4 | `PATCH` description, `If-Match: "1"` | 200, `ETag: "2"` | version 2, new description, `updated_at` moved |
| 5 | Same `PATCH` with stale `If-Match: "1"` | 412, `type` ends in `version-mismatch` | Unchanged: version 2, description from step 4 |
| 6 | Add labour line 2.5 × 650, `If-Match: "2"` | 200, `ETag: "3"`, status `InProgress`, total 1625 | `job_lines`: 1 row. `jobs.status` = `InProgress`, version 3 |
| 7 | Add material line 10 × 12.35, `If-Match: "3"` | `ETag: "4"`, total 1748.50 | `job_lines`: 2 rows |
| 8 | `PATCH` labour line quantity 3, `If-Match: "4"` | `ETag: "5"`, total 2073.50 | Labour line `quantity` = 3.000. Job version 5: a line change bumps the job |
| 9 | `DELETE` material line, `If-Match: "5"` | `ETag: "6"`, total 1950.00 | `job_lines`: 1 row |
| 10 | Complete, `If-Match: "6"` | `ETag: "7"`, status `Completed`, `export.status` `Pending`, attempts 0 | `jobs`: `Completed`, version 7, `completed_at` set. `export_tasks`: 1 row, `Pending`, attempts 0, `lease_token`/`locked_by` null. Job and task were written in the same transaction |
| 11 | `DELETE` the remaining line, `If-Match: "7"` | 409, `type` ends in `job-state-conflict` | Unchanged: the job is frozen |
| 14 | `PATCH` without `If-Match` | 428, `type` ends in `precondition-required` | Unchanged |

Now start a worker in a second terminal:

```bash
dotnet run --project src/Jobs.Worker
```

| # | Request | Expect | DB check |
| --- | --- | --- | --- |
| 12 | `GET /jobs/{id}` after a second or two | status `Exported`, `export.status` `Succeeded`, `externalInvoiceId` set, `ETag: "8"` | `export_tasks`: `Succeeded`, attempts ≥ 1 (more if the fake failed once), `locked_by` = `<machine>-<pid>`, `external_invoice_id` set. `jobs`: `Exported`, version 8 |
| 13 | `GET /jobs?status=Exported` | The job is listed | — |

Check exactly one invoice for the job, matching the job total:

```sql
SELECT i.id, i.idempotency_key, i.job_id, i.total
FROM fake_accounting.invoices i;
-- 1 row: total 1950.00, idempotency_key = export_tasks.id, id = export_tasks.external_invoice_id
```

Worker logs are JSON. Each export line carries `TaskId`, `JobId`, `WorkerId` and `Attempt` in its scopes. The default fake fails 10 % of calls before writing and 5 % after, so `Retrying in …` or `replayed: true` may appear. Both are expected.

## 2. Error contract

| Case | Request | Expect |
| --- | --- | --- |
| Key reused, different body | `POST /jobs` with `demo-job-0001` and another `customerName` | 422, `idempotency-key-reused`; no new row in `jobs` |
| Missing key | `POST /jobs` without `Idempotency-Key` | 400, `idempotency-key-missing` |
| Invalid line | Add line with `quantity: 0`, `unitPrice: 1.234` | 400, `validation-error`, `errors.quantity` and `errors.unitPrice` |
| Malformed `If-Match` | `If-Match: W/"1"` or `If-Match: 1` | 400, `invalid-if-match` |
| Unknown line | `DELETE /jobs/{id}/lines/{random-guid}` with the current ETag | 404, `line-not-found` |
| Complete without lines | Complete a fresh Draft job (`If-Match: "1"`) | 409, `job-state-conflict`; no `export_tasks` row |

## 3. Failure scenarios (the exactly-once story)

Each scenario needs a fresh job: create (new Idempotency-Key) → add a line → complete. Stop the running worker (Ctrl+C) first. Clear an environment variable afterwards with `Remove-Item Env:<name>`.

### A. Accounting down: retries, then dead-letter

```powershell
$env:FakeAccounting__FailBeforeRate = "1"; dotnet run --project src/Jobs.Worker
```

- **Watch:** `attempts` goes 1 → 5. `next_attempt_at` moves out by about 2, 4, 8, then 16 s. `last_error` reads "Simulated outage". `locked_by`/`lease_token` are cleared between attempts.
- **End state:** after 5 attempts, `export_tasks.status` = `DeadLettered` and `jobs.status` = `ExportFailed`. There are **no** invoices for the job, and the API shows `export.lastError`.

### B. Lost response: the idempotency key prevents a second invoice

```powershell
$env:FakeAccounting__FailAfterRate = "0.6"; dotnet run --project src/Jobs.Worker
```

- **Watch:** the worker logs "Simulated lost response", retries, and finally logs `replayed: true`.
- **End state:** the task is `Succeeded` with attempts > 1. There is **exactly one** invoice even though accounting was called several times:

```sql
SELECT job_id, count(*) FROM fake_accounting.invoices GROUP BY job_id HAVING count(*) > 1;
-- 0 rows
```

With `FailAfterRate = "1"`, every call writes and then fails. The task dead-letters after 5 attempts with exactly one invoice. That is the reconciliation case listed in the README's known limitations.

### C. Stale worker is fenced (lease takeover)

Terminal 2, a slow worker whose call outlives the 30 s lease:

```powershell
$env:FakeAccounting__LatencyMin = "00:00:40"; $env:FakeAccounting__LatencyMax = "00:00:40"; dotnet run --project src/Jobs.Worker
```

1. Complete a job. Wait until `export_tasks.locked_by` shows the slow worker and `lease_until` is about 30 s ahead.
2. Terminal 3, a normal worker:

   ```bash
   dotnet run --project src/Jobs.Worker --no-build
   ```

3. About 30 s after the claim, the normal worker takes the task over: `attempts` 2, a new `lease_token`, its own `locked_by`. It exports right away, and the task becomes `Succeeded`.
4. About 40 s after the claim, the slow worker finishes. It logs **"Lease lost before completion"**, because its fenced update matched 0 rows. Its call returned the existing invoice through the idempotency key.
5. **End state:** one invoice; the task is `Succeeded` with `locked_by` = the normal worker.

### D. Competing workers

Run two or three normal workers (the 2nd and 3rd with `--no-build`) and complete several jobs quickly. Each task is processed by one worker (`SKIP LOCKED`), and the duplicate-invoice query above returns 0 rows.

## 4. Reset between runs

```sql
TRUNCATE jobs, job_lines, export_tasks, idempotency_keys, fake_accounting.invoices CASCADE;
```

Or change the `Idempotency-Key` in `requests.http`. Its ETags assume a new job each run.
