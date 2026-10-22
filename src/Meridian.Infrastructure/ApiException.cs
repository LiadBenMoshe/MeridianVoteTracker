namespace Meridian.Infrastructure;

/// <summary>An expected business error. Rendered as RFC 7807 ProblemDetails by the API.</summary>
public class ApiException(int status, string title, string? detail = null, IDictionary<string, object?>? extensions = null)
    : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public IDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();

    public static ApiException NotFound(string what) => new(404, "Not found", $"{what} was not found.");
}

/// <summary>Field-level validation errors (shown next to the field in the UI).</summary>
public sealed class FieldValidationException(IDictionary<string, string[]> errors)
    : ApiException(400, "Validation failed", "One or more fields are invalid.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public static FieldValidationException For(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
