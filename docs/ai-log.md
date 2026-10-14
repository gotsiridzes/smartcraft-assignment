# AI usage log

Each entry has four parts: what was asked, what came back, what was wrong or changed, and how it is verified. The assistant was Claude (Claude Code).

## Planning

| Asked | Came back | Wrong / changed | Verified by |
| --- | --- | --- | --- |
| Review the v1 implementation plan | A list of defects and a final plan ([final plan](Craft%20Jobs%20Service%20—%20Final%20Implementation%20Plan.md)) | Accepted. Line edit/delete moved into scope at my request. | Review of the reasoning; the items below are checked by tests |
| Is `xmin` or an explicit column better for concurrency? | Both work. Explicit `Version` was chosen for clarity, portability, and use as the ETag | — | Npgsql docs; test 3 |
| Why could test 1 be flaky? | 20 % failures with MaxAttempts 5: P(dead-letter) = 0.2⁵ per task. Over 200 tasks, about 6 % of runs fail | Test options use MaxAttempts 50 and millisecond backoff | Arithmetic; repeated runs of test 1 |
| Does test 1 prove the idempotency key? | No: failures only happened before the invoice was written, and "no duplicate keys" is guaranteed by the unique index anyway | The fake gained fail-after; the test asserts one invoice per job and `ReplayCount > 0` | Test 1 |
| EF Core with ids generated in the domain | A new child with its key already set is tracked as Modified under the default `ValueGeneratedOnAdd`, so every add-line would be a false 412 | `ValueGeneratedNever()` on every Guid key | Test 3 (`Line_changes_bump_the_job_version`); every add-line and complete test |
| Worker that crashes every time | v1 counted attempts only on handled failures, so the task looped forever | Attempts are counted in the claim statement | `ExportTests.A_task_that_keeps_losing_its_worker_is_dead_lettered` |

## Implementation

The code was written by the assistant without compiling or running it (my instruction). The first `dotnet build` and `dotnet test` are the verification for everything below.

| Area | What AI produced | Wrong / changed during writing | Verified by |
| --- | --- | --- | --- |
| Claim SQL (`TaskClaimer`) | CTE with `FOR UPDATE SKIP LOCKED`, lease token, `attempts + 1`, DB clock | — | Tests 1 and 2, not by reading |
| Fenced outcome writes | `ExecuteUpdateAsync` for success and dead-letter (in a transaction with the job update); plain SQL for retry so `next_attempt_at` uses the database clock | The plan had the retry time from the app clock; switched to `now() + backoff` so the queue uses one clock | Test 2 (stale holder's complete, fail and dead-letter all return false) |
| Fake accounting | `INSERT … ON CONFLICT DO NOTHING RETURNING id`, own connection, fail-before / fail-after / permanent | — | Tests 1 and 2 |
| Idempotency-Key | Key row in the job's transaction; replay on PK violation; 422 for a different body | The plan stored the response as `jsonb`, but `jsonb` reorders keys, so a replay would not be byte-identical. Changed to `text` | Test 4 compares raw bodies |
| Endpoints | Shared write path: 428/400 → 404 → 412 → domain → save (`DbUpdateConcurrencyException` → 412) | First draft had an ambiguous `JsonOptions` (`Http.Json` vs `Mvc`); aliased. A concurrent complete can surface as a unique violation on `export_tasks.job_id` instead of a concurrency exception, so that also maps to 412 | Tests 3 and 6 |
| Validation | Rules in the domain, field errors as 400 | The plan suggested .NET 10 `AddValidation()`. Kept the rules on `Job` instead, so there is one source of truth | Test 5; `HeaderContractTests.An_invalid_line_returns_400_with_field_errors` |
| Claim result mapping | Plain `NpgsqlCommand` instead of `Database.SqlQuery<T>` | `SqlQuery<T>` for unmapped types interacts with the snake_case naming convention in ways I could not verify without running it | Tests 1 and 2 |
| Initial migration | Hand-written migration, designer and model snapshot (no build allowed, so `dotnet ef` could not generate them) | Must match the model exactly, or EF reports pending model changes | `dotnet ef migrations has-pending-model-changes -p src/Jobs.Core -s src/Jobs.Api`; the test fixture applies the migration |
