using Meridian.Api.Contracts;
using Meridian.Api.Infrastructure;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UsersController(MeridianDbContext db) : ControllerBase
{
    /// <summary>Predefined users for the user switcher.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<List<UserDto>>> List(CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(u => u.Role).ThenBy(u => u.Name)
            .Select(u => new UserDto(u.Id, u.Name, u.Role)).ToListAsync(ct);

    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var id = User.UserId();
        var u = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("User");
        return new UserDto(u.Id, u.Name, u.Role);
    }
}
