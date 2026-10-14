using System.Net;
using Jobs.Api.Http;

namespace Jobs.Tests.Integration;

/// <summary>Test 6: the HTTP contract for preconditions, keys and frozen jobs.</summary>
[Collection(PostgresCollection.Name)]
public sealed class HeaderContractTests(PostgresFixture postgres) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await postgres.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_write_without_if_match_returns_428()
    {
        var client = postgres.CreateApiClient();
        var (jobId, _) = await JobsApi.CreateJobAsync(client);

        using var response = await JobsApi.SendAsync(client, HttpMethod.Patch, $"/jobs/{jobId}", ifMatch: null, new { description = "No precondition" });

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await JobsApi.ProblemTypeOf(response)).ShouldBe(ProblemTypes.PreconditionRequired);
    }

    [Theory]
    [InlineData("W/\"1\"")]
    [InlineData("1")]
    [InlineData("\"one\"")]
    [InlineData("\"1\", \"2\"")]
    public async Task A_malformed_if_match_returns_400(string ifMatch)
    {
        var client = postgres.CreateApiClient();
        var (jobId, _) = await JobsApi.CreateJobAsync(client);

        using var response = await JobsApi.SendAsync(client, HttpMethod.Patch, $"/jobs/{jobId}", ifMatch, new { description = "Bad header" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JobsApi.ProblemTypeOf(response)).ShouldBe(ProblemTypes.InvalidIfMatch);
    }

    [Fact]
    public async Task A_create_without_idempotency_key_returns_400()
    {
        var client = postgres.CreateApiClient();

        using var response = await JobsApi.CreateAsync(client, JobsApi.NewJob(), idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JobsApi.ProblemTypeOf(response)).ShouldBe(ProblemTypes.IdempotencyKeyMissing);
    }

    [Fact]
    public async Task Completing_a_job_without_lines_returns_409()
    {
        var client = postgres.CreateApiClient();
        var (jobId, etag) = await JobsApi.CreateJobAsync(client);

        using var response = await JobsApi.SendAsync(client, HttpMethod.Post, $"/jobs/{jobId}/complete", etag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JobsApi.ProblemTypeOf(response)).ShouldBe(ProblemTypes.JobStateConflict);
    }

    [Fact]
    public async Task Deleting_a_line_of_a_completed_job_returns_409()
    {
        var client = postgres.CreateApiClient();
        var (jobId, lineId, etag) = await CompletedJobAsync(client);

        using var response = await JobsApi.SendAsync(client, HttpMethod.Delete, $"/jobs/{jobId}/lines/{lineId}", etag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JobsApi.ProblemTypeOf(response)).ShouldBe(ProblemTypes.JobStateConflict);
    }

    [Fact]
    public async Task Completing_queues_the_export_and_returns_its_status()
    {
        var client = postgres.CreateApiClient();
        var (jobId, _, _) = await CompletedJobAsync(client);

        using var response = await client.GetAsync($"/jobs/{jobId}");
        var json = await JobsApi.ReadJsonAsync(response);

        json.GetProperty("status").GetString().ShouldBe("Completed");
        json.GetProperty("export").GetProperty("status").GetString().ShouldBe("Pending");
    }

    [Fact]
    public async Task Editing_a_line_with_a_stale_etag_returns_412()
    {
        var client = postgres.CreateApiClient();
        var (jobId, etag) = await JobsApi.CreateJobAsync(client);

        using var added = await JobsApi.SendAsync(client, HttpMethod.Post, $"/jobs/{jobId}/lines", etag, JobsApi.LabourLine());
        var lineId = (await JobsApi.ReadJsonAsync(added)).GetProperty("lines")[0].GetProperty("id").GetGuid();

        // etag is "1"; adding the line moved the job to "2".
        using var response = await JobsApi.SendAsync(client, HttpMethod.Patch, $"/jobs/{jobId}/lines/{lineId}", etag, new { quantity = 3m });

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await JobsApi.ProblemTypeOf(response)).ShouldBe(ProblemTypes.VersionMismatch);
    }

    [Fact]
    public async Task An_unknown_line_returns_404()
    {
        var client = postgres.CreateApiClient();
        var (jobId, etag) = await JobsApi.CreateJobAsync(client);

        using var response = await JobsApi.SendAsync(client, HttpMethod.Delete, $"/jobs/{jobId}/lines/{Guid.NewGuid()}", etag);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await JobsApi.ProblemTypeOf(response)).ShouldBe(ProblemTypes.LineNotFound);
    }

    [Fact]
    public async Task An_invalid_line_returns_400_with_field_errors()
    {
        var client = postgres.CreateApiClient();
        var (jobId, etag) = await JobsApi.CreateJobAsync(client);

        using var response = await JobsApi.SendAsync(client, HttpMethod.Post, $"/jobs/{jobId}/lines", etag,
            new { type = "Material", description = "Cable", quantity = 0m, unitPrice = 1.234m });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var json = await JobsApi.ReadJsonAsync(response);
        json.GetProperty("type").GetString().ShouldBe(ProblemTypes.Validation);
        json.GetProperty("errors").TryGetProperty("quantity", out _).ShouldBeTrue();
        json.GetProperty("errors").TryGetProperty("unitPrice", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_body_that_is_not_json_returns_400()
    {
        var client = postgres.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/jobs")
        {
            Content = new StringContent("{ \"customerName\": \"Fjord\" }\n@jobId = 1", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JobsApi.ProblemTypeOf(response)).ShouldBe(ProblemTypes.InvalidRequest);
    }

    private static async Task<(Guid JobId, Guid LineId, string ETag)> CompletedJobAsync(HttpClient client)
    {
        var (jobId, etag) = await JobsApi.CreateJobAsync(client);

        using var added = await JobsApi.SendAsync(client, HttpMethod.Post, $"/jobs/{jobId}/lines", etag, JobsApi.LabourLine());
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lineId = (await JobsApi.ReadJsonAsync(added)).GetProperty("lines")[0].GetProperty("id").GetGuid();

        using var completed = await JobsApi.SendAsync(client, HttpMethod.Post, $"/jobs/{jobId}/complete", JobsApi.ETagOf(added));
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (jobId, lineId, JobsApi.ETagOf(completed));
    }
}
