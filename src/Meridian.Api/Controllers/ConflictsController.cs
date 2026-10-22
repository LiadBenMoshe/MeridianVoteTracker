using Meridian.Api.Contracts;
using Meridian.Api.Infrastructure;
using Meridian.Api.Services;
using Meridian.Core.Domain;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Controllers;

[ApiController]
[Route("api/conflicts")]
[Authorize(Policy = Policies.Analyst)]
public sealed class ConflictsController(MeridianDbContext db, ConflictService service) : ControllerBase
{
    /// <summary>Conflicts, pending by default, filterable by vote (CON-01). Resolved ones stay visible (CON-06).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<ConflictDto>>> List([FromQuery] ConflictStatus? status, [FromQuery] string? voteId,
        [FromQuery] bool resolved = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var q = db.Conflicts.AsNoTracking();
        if (status is not null) q = q.Where(c => c.Status == status);
        else q = resolved ? q.Where(c => c.Status != ConflictStatus.Pending) : q.Where(c => c.Status == ConflictStatus.Pending);
        if (!string.IsNullOrWhiteSpace(voteId)) q = q.Where(c => c.VoteId.Contains(voteId.Trim()));
        var total = await q.CountAsync(ct);
        var items = await (resolved ? q.OrderByDescending(c => c.DecidedAt) : q.OrderBy(c => c.VoteId).ThenBy(c => c.Id))
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<ConflictDto>(await ConflictMapper.ToDtosAsync(db, items, ct), total, page, pageSize);
    }

    /// <summary>Approved vs incoming value side by side, with the source of each (CON-02).</summary>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<ConflictDto>> Get(long id, CancellationToken ct)
    {
        var c = await db.Conflicts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound($"Conflict {id}");
        return (await ConflictMapper.ToDtosAsync(db, [c], ct))[0];
    }

    /// <summary>
    /// Accept (incoming becomes approved) or reject (approved is kept) (CON-04).
    /// 409 when someone else already decided; the body's "current" holds the latest state (CON-05).
    /// </summary>
    [HttpPost("{id:long}/decision")]
    [ProducesResponseType<ConflictDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ConflictDto>> Decide(long id, [FromBody] DecisionRequest request, CancellationToken ct) =>
        await service.DecideAsync(id, request.Decision!.Value, request.Comment, User.UserId(), ct);
}

[ApiController]
[Route("api/review")]
[Authorize(Policy = Policies.Analyst)]
public sealed class ReviewController(MeridianDbContext db) : ControllerBase
{
    /// <summary>Counts for the Review inbox badge.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ReviewSummaryDto>> Summary(CancellationToken ct) => new ReviewSummaryDto(
        await db.Conflicts.CountAsync(c => c.Status == ConflictStatus.Pending, ct),
        await db.Extractions.CountAsync(e => e.Status == ExtractionStatus.NeedsReview, ct),
        await db.Extractions.CountAsync(e => e.Status == ExtractionStatus.Queued || e.Status == ExtractionStatus.Running, ct));
}
