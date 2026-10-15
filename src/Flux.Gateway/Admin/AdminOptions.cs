namespace Flux.Gateway.Admin;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    public const int MinTokenLength = 32;

    /// <summary>Shared secret for X-Admin-Token. Unset or empty means the admin API is not mapped at all.</summary>
    public string? Token { get; set; }

    public bool Enabled => !string.IsNullOrEmpty(Token);

    public bool IsValid => !Enabled || Token!.Length >= MinTokenLength;
}
