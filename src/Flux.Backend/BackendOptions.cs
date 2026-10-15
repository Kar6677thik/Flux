using System.ComponentModel.DataAnnotations;

namespace Flux.Backend;

public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    /// <summary>Which fake service this process plays: "payments" or "refunds".</summary>
    [Required]
    [RegularExpression("^(payments|refunds)$", ErrorMessage = "Backend:ServiceName must be 'payments' or 'refunds'.")]
    public string ServiceName { get; set; } = "";

    /// <summary>Artificial work per request, so the backend behaves a little like a real service.</summary>
    [Range(0, 10_000)]
    public int LatencyMs { get; set; } = 5;
}
