using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jobs.Tests.Integration;

/// <summary>Small HTTP helpers so tests read as the API contract.</summary>
internal static class JobsApi
{
    public static object NewJob(string customerName = "Fjord Elektro AS") => new
    {
        customerName,
        siteAddress = "Storgata 1, 0155 Oslo",
        description = "Rewire kitchen",
    };

    public static object LabourLine(decimal quantity = 2.5m) => new
    {
        type = "Labour",
        description = "Electrician hours",
        quantity,
        unitPrice = 650m,
    };

    public static async Task<HttpResponseMessage> CreateAsync(HttpClient client, object body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/jobs") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Creates a job and returns its id and ETag (always <c>"1"</c> for a new job).</summary>
    public static async Task<(Guid Id, string ETag)> CreateJobAsync(HttpClient client)
    {
        using var response = await CreateAsync(client, NewJob(), Guid.NewGuid().ToString());
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await ReadJsonAsync(response);
        return (json.GetProperty("id").GetGuid(), ETagOf(response));
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? ifMatch, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    public static string ETagOf(HttpResponseMessage response)
        => response.Headers.ETag?.Tag ?? throw new InvalidOperationException($"Response {(int)response.StatusCode} has no ETag.");

    /// <summary>
    /// Buffers the body first, so a test can read the same response more than once
    /// (an in-memory test-server stream can otherwise be read only once).
    /// </summary>
    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The ProblemDetails <c>type</c>, which tells error kinds apart.</summary>
    public static async Task<string?> ProblemTypeOf(HttpResponseMessage response)
    {
        var json = await ReadJsonAsync(response);
        return json.TryGetProperty("type", out var type) ? type.GetString() : null;
    }
}
