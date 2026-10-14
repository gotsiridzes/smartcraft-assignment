using System.Net;
using Jobs.Api.Http;
using Microsoft.EntityFrameworkCore;

namespace Jobs.Tests.Integration;

/// <summary>Test 4: a retried POST /jobs never creates a second job.</summary>
[Collection(PostgresCollection.Name)]
public sealed class IdempotencyTests(PostgresFixture postgres) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await postgres.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Parallel_creates_with_the_same_key_create_one_job_and_identical_responses()
    {
        var client = postgres.CreateApiClient();
        var key = Guid.NewGuid().ToString();
        var body = JobsApi.NewJob();

        // The second insert blocks on the key's primary key until the first commits, then replays.
        var responses = await Task.WhenAll(
            JobsApi.CreateAsync(client, body, key),
            JobsApi.CreateAsync(client, body, key));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        bodies[1].ShouldBe(bodies[0]);
        JobsApi.ETagOf(responses[1]).ShouldBe(JobsApi.ETagOf(responses[0]));
        responses[1].Headers.Location.ShouldBe(responses[0].Headers.Location);

        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.IdempotencyKeys.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_sequential_retry_replays_the_first_response()
    {
        var client = postgres.CreateApiClient();
        var key = Guid.NewGuid().ToString();

        using var first = await JobsApi.CreateAsync(client, JobsApi.NewJob(), key);
        using var retry = await JobsApi.CreateAsync(client, JobsApi.NewJob(), key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues("Idempotent-Replayed").ShouldContain("true");
        (await retry.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());

        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_returns_422()
    {
        var client = postgres.CreateApiClient();
        var key = Guid.NewGuid().ToString();

        using var first = await JobsApi.CreateAsync(client, JobsApi.NewJob("First customer"), key);
        using var reused = await JobsApi.CreateAsync(client, JobsApi.NewJob("Another customer"), key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await JobsApi.ProblemTypeOf(reused)).ShouldBe(ProblemTypes.IdempotencyKeyReused);

        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }
}
