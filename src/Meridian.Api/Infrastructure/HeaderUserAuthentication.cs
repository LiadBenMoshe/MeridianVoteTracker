using System.Security.Claims;
using System.Text.Encodings.Web;
using Meridian.Core.Domain;
using Meridian.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meridian.Api.Infrastructure;

/// <summary>
/// Simplified identity (allowed by the assignment): the UI sends the selected user in the X-User header.
/// Roles come from the users table and are enforced by [Authorize] policies on the API, not by hiding buttons.
/// </summary>
public sealed class HeaderUserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    MeridianDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "HeaderUser";
    public const string HeaderName = "X-User";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || string.IsNullOrWhiteSpace(values.ToString()))
            return AuthenticateResult.NoResult();

        var id = values.ToString().Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        if (user is null) return AuthenticateResult.Fail($"Unknown user '{id}'");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class Policies
{
    /// <summary>Analysts and the admin: imports, conflicts, snapshots, extractions.</summary>
    public const string Analyst = "AnalystOrAdmin";
    /// <summary>Admin only: positions/weights, publishing snapshots.</summary>
    public const string Admin = "AdminOnly";

    public static void Configure(Microsoft.AspNetCore.Authorization.AuthorizationOptions o)
    {
        o.AddPolicy(Analyst, p => p.RequireRole(Roles.Analyst, Roles.Admin));
        o.AddPolicy(Admin, p => p.RequireRole(Roles.Admin));
        o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}

public static class UserExtensions
{
    public static string UserId(this ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}
