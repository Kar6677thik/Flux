using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flux.Gateway.IntegrationTests;

/// <summary>Asserts the error contract from spec §7.4: status, Problem Details media type, code, request id.</summary>
public static class Problems
{
    /// <returns>The parsed body, because a test server's response stream can only be read once.</returns>
    public static async Task<JsonElement> AssertAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal($"https://flux.local/errors/{code}", problem.GetProperty("type").GetString());
        Assert.Equal(
            Assert.Single(response.Headers.GetValues("X-Request-Id")),
            problem.GetProperty("requestId").GetString());
        return problem;
    }
}
