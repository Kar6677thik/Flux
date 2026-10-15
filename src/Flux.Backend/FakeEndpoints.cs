using Microsoft.Extensions.Options;

namespace Flux.Backend;

public sealed record CreatePaymentRequest(long Amount, string? Currency);
public sealed record CreateRefundRequest(string? PaymentId, long Amount);

/// <summary>
/// Deliberately fake and stateless: the gateway is what we are studying, so the backends only
/// need to look plausible and be cheap. GET returns a made-up object for any id.
/// </summary>
public static class FakeEndpoints
{
    public static void MapPayments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/payments").AddEndpointFilter(MerchantContext.RequireMerchant);

        group.MapPost("/", async (CreatePaymentRequest body, HttpContext http, IOptions<BackendOptions> options) =>
        {
            if (body.Amount <= 0)
                return Invalid("amount must be a positive integer (in paise).");
            var currency = body.Currency ?? "INR";
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
                return Invalid("currency must be a 3-letter upper-case code such as INR.");

            await SimulateWork(options.Value, http.RequestAborted);
            var payment = new
            {
                id = FakeIds.New("pay"),
                merchantId = http.MerchantId(),
                amount = body.Amount,
                currency,
                status = "created",
                servedBy = options.Value.ServiceName,
            };
            return Results.Created($"/payments/{payment.id}", payment);
        });

        group.MapGet("/{id}", async (string id, HttpContext http, IOptions<BackendOptions> options) =>
        {
            await SimulateWork(options.Value, http.RequestAborted);
            return Results.Ok(new
            {
                id,
                merchantId = http.MerchantId(),
                amount = 50_000,
                currency = "INR",
                status = "captured",
                servedBy = options.Value.ServiceName,
            });
        });
    }

    public static void MapRefunds(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/refunds").AddEndpointFilter(MerchantContext.RequireMerchant);

        group.MapPost("/", async (CreateRefundRequest body, HttpContext http, IOptions<BackendOptions> options) =>
        {
            if (string.IsNullOrWhiteSpace(body.PaymentId) || !body.PaymentId.StartsWith("pay_", StringComparison.Ordinal))
                return Invalid("paymentId must be a payment id such as pay_8fK2mQ9xLp3Za1.");
            if (body.Amount <= 0)
                return Invalid("amount must be a positive integer (in paise).");

            await SimulateWork(options.Value, http.RequestAborted);
            var refund = new
            {
                id = FakeIds.New("rfnd"),
                merchantId = http.MerchantId(),
                paymentId = body.PaymentId,
                amount = body.Amount,
                status = "processed",
                servedBy = options.Value.ServiceName,
            };
            return Results.Created($"/refunds/{refund.id}", refund);
        });

        group.MapGet("/{id}", async (string id, HttpContext http, IOptions<BackendOptions> options) =>
        {
            await SimulateWork(options.Value, http.RequestAborted);
            return Results.Ok(new
            {
                id,
                merchantId = http.MerchantId(),
                paymentId = "pay_0000000000000",
                amount = 1_000,
                status = "processed",
                servedBy = options.Value.ServiceName,
            });
        });
    }

    private static Task SimulateWork(BackendOptions options, CancellationToken ct) =>
        options.LatencyMs > 0 ? Task.Delay(options.LatencyMs, ct) : Task.CompletedTask;

    private static IResult Invalid(string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid request",
        detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });
}
