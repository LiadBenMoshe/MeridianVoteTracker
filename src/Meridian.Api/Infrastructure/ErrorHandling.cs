using System.Diagnostics;
using Meridian.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meridian.Api.Infrastructure;

/// <summary>
/// One error format for the whole API: RFC 7807 ProblemDetails, always with correlationId.
/// Validation errors use ValidationProblemDetails ("errors": { field: [messages] }).
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem = exception switch
        {
            FieldValidationException v => new ValidationProblemDetails(v.Errors) { Status = 400, Title = v.Title, Detail = v.Detail },
            ApiException a => new ProblemDetails { Status = a.Status, Title = a.Title, Detail = a.Detail },
            DbUpdateConcurrencyException => new ProblemDetails
            {
                Status = 409, Title = "Changed by someone else",
                Detail = "This item was changed by another user. Reload to see the current state.",
            },
            BadHttpRequestException b => new ProblemDetails { Status = b.StatusCode, Title = "Bad request", Detail = b.Message },
            _ => new ProblemDetails { Status = 500, Title = "Unexpected error", Detail = "Something went wrong. Quote the correlation id when reporting it." },
        };

        if (exception is ApiException api)
            foreach (var (k, v) in api.Extensions) problem.Extensions[k] = v;

        if (problem.Status >= 500) log.LogError(exception, "Unhandled error");
        else log.LogInformation("Request failed with {Status}: {Title}", problem.Status, problem.Title);

        http.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = exception });
    }

    public static void Customize(ProblemDetailsContext ctx)
    {
        ctx.ProblemDetails.Extensions["correlationId"] = Correlation.Id ?? ctx.HttpContext.TraceIdentifier;
        ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier;
        ctx.ProblemDetails.Type ??= $"https://httpstatuses.io/{ctx.ProblemDetails.Status}";
        if (ctx.ProblemDetails.Status == 401)
        {
            ctx.ProblemDetails.Title = "Not signed in";
            ctx.ProblemDetails.Detail ??= "Select a user (X-User header: dana, omar or lena).";
        }
        if (ctx.ProblemDetails.Status == 403)
        {
            ctx.ProblemDetails.Title = "Not allowed";
            ctx.ProblemDetails.Detail ??= "Your role is not allowed to do this.";
        }
    }

    public static bool IsUniqueViolation(Exception ex, string? constraint = null) =>
        ex.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        (constraint is null || pg.ConstraintName == constraint);
}

/// <summary>
/// Reads X-Correlation-Id from the UI (or creates one), returns it, and puts it in every log line.
/// Background jobs store it so the trail continues to Azure AI calls.
/// </summary>
public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> log)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var id = http.Request.Headers[Correlation.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) id = Correlation.NewId();
        Correlation.Id = id;
        http.TraceIdentifier = id;
        http.Response.Headers[Correlation.HeaderName] = id;
        Activity.Current?.SetTag("correlation.id", id);
        Activity.Current?.AddBaggage("correlation.id", id);
        // Application Insights creates the request item before this middleware runs, so tag it here.
        if (http.Features.Get<Microsoft.ApplicationInsights.DataContracts.RequestTelemetry>() is { } request)
            request.Properties["CorrelationId"] = id;

        using (log.BeginScope("CorrelationId:{CorrelationId}", id))
        {
            await next(http);
        }
    }
}
