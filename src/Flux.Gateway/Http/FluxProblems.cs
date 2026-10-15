using Microsoft.AspNetCore.Mvc;

namespace Flux.Gateway.Http;

/// <summary>
/// The gateway's error contract (spec §7.4): RFC 9457 Problem Details with a stable machine-readable
/// "code" and the request id, so a client can quote it when asking for support.
/// </summary>
public static class FluxProblems
{
    public static Task WriteAsync(HttpContext http, int status, string code, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Type = $"https://flux.local/errors/{code}",
            Title = title,
            Status = status,
            Detail = detail,
            Extensions =
            {
                ["code"] = code,
                ["requestId"] = http.GetRequestId(),
            },
        };

        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}

/// <summary>The same contract as an <see cref="IResult"/>, for endpoints that return results.</summary>
public sealed record FluxProblem(int Status, string Code, string Title, string Detail) : IResult
{
    public Task ExecuteAsync(HttpContext http) => FluxProblems.WriteAsync(http, Status, Code, Title, Detail);

    public static FluxProblem InvalidRequest(string detail) =>
        new(StatusCodes.Status400BadRequest, "invalid_request", "Invalid request", detail);
}
