namespace Meridian.Infrastructure;

/// <summary>
/// Correlation id for the current flow. Set by the HTTP middleware for requests and
/// restored by background workers from the job row, so logs and Azure AI calls share one id.
/// </summary>
public static class Correlation
{
    public const string HeaderName = "X-Correlation-Id";
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Id
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..16];
}
