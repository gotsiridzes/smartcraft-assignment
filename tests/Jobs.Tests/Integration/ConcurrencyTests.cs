using System.Net;
using Jobs.Api.Http;

namespace Jobs.Tests.Integration;

/// <summary>Test 3: two editors with the same ETag never silently overwrite each other.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcurrencyTests(PostgresFixture postgres) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await postgres.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Parallel_patches_with_the_same_etag_produce_one_success_and_one_412()
    {
        var client = postgres.CreateApiClient();
        var (jobId, etag) = await JobsApi.CreateJobAsync(client);
        etag.ShouldBe("\"1\"");

        // Both requests read version 1. Whichever loses either sees version 2 on its own read
        // or has its UPDATE ... WHERE version = 1 match no row; both paths must end in 412.
        var responses = await Task.WhenAll(
            JobsApi.SendAsync(client, HttpMethod.Patch, $"/jobs/{jobId}", etag, new { customerName = "Alice" }),
            JobsApi.SendAsync(client, HttpMethod.Patch, $"/jobs/{jobId}", etag, new { customerName = "Bob" }));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.PreconditionFailed);
        (await JobsApi.ProblemTypeOf(loser)).ShouldBe(ProblemTypes.VersionMismatch);

        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var winnerName = (await JobsApi.ReadJsonAsync(winner)).GetProperty("customerName").GetString();

        using var current = await client.GetAsync($"/jobs/{jobId}");
        JobsApi.ETagOf(current).ShouldBe("\"2\"");
        (await JobsApi.ReadJsonAsync(current)).GetProperty("customerName").GetString().ShouldBe(winnerName);
    }

    [Fact]
    public async Task A_write_with_a_stale_etag_returns_412_and_changes_nothing()
    {
        var client = postgres.CreateApiClient();
        var (jobId, staleEtag) = await JobsApi.CreateJobAsync(client);

        using var first = await JobsApi.SendAsync(client, HttpMethod.Patch, $"/jobs/{jobId}", staleEtag, new { description = "First edit" });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        JobsApi.ETagOf(first).ShouldBe("\"2\"");

        using var second = await JobsApi.SendAsync(client, HttpMethod.Patch, $"/jobs/{jobId}", staleEtag, new { description = "Second edit" });
        second.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        using var current = await client.GetAsync($"/jobs/{jobId}");
        JobsApi.ETagOf(current).ShouldBe("\"2\"");
        (await JobsApi.ReadJsonAsync(current)).GetProperty("description").GetString().ShouldBe("First edit");
    }

    [Fact]
    public async Task Line_changes_bump_the_job_version()
    {
        var client = postgres.CreateApiClient();
        var (jobId, etag) = await JobsApi.CreateJobAsync(client);

        using var added = await JobsApi.SendAsync(client, HttpMethod.Post, $"/jobs/{jobId}/lines", etag, JobsApi.LabourLine());
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        JobsApi.ETagOf(added).ShouldBe("\"2\"");
        var addedJson = await JobsApi.ReadJsonAsync(added);
        addedJson.GetProperty("status").GetString().ShouldBe("InProgress");
        var lineId = addedJson.GetProperty("lines")[0].GetProperty("id").GetGuid();

        using var edited = await JobsApi.SendAsync(client, HttpMethod.Patch, $"/jobs/{jobId}/lines/{lineId}", "\"2\"", new { quantity = 4m });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
        JobsApi.ETagOf(edited).ShouldBe("\"3\"");
        (await JobsApi.ReadJsonAsync(edited)).GetProperty("total").GetDecimal().ShouldBe(2600.00m);

        using var removed = await JobsApi.SendAsync(client, HttpMethod.Delete, $"/jobs/{jobId}/lines/{lineId}", "\"3\"");
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        JobsApi.ETagOf(removed).ShouldBe("\"4\"");
        var removedJson = await JobsApi.ReadJsonAsync(removed);
        removedJson.GetProperty("lines").GetArrayLength().ShouldBe(0);
        removedJson.GetProperty("total").GetDecimal().ShouldBe(0m);
    }
}
