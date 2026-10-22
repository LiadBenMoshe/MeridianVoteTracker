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
[Route("api/votes")]
[Authorize(Policy = Policies.Analyst)]
public sealed class VotesController(MeridianDbContext db, VoteService service) : ControllerBase
{
    /// <summary>Votes with position, weight and open conflicts. Votes without a position are counted separately (POS-04).</summary>
    [HttpGet]
    public async Task<ActionResult<VoteListDto>> List([FromQuery] int? session, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? search, [FromQuery] bool withoutPosition = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var q = Filter(db.Votes.AsNoTracking(), session, from, to, search);
        var withoutTotal = await q.CountAsync(v => v.Position == null, ct);
        if (withoutPosition) q = q.Where(v => v.Position == null);
        var total = await q.CountAsync(ct);
        var items = await Project(db, q.OrderBy(v => v.VoteDate).ThenBy(v => v.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
        return new VoteListDto(items, total, page, pageSize, withoutTotal);
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<List<int>>> Sessions(CancellationToken ct) =>
        await db.Votes.AsNoTracking().Select(v => v.Session).Distinct().OrderBy(s => s).ToListAsync(ct);

    /// <summary>One vote: details, position, country votes (with a marker when changed by a decision).
    /// Vote ids contain '/', so all per-vote endpoints take the id as a query parameter.</summary>
    [HttpGet("detail")]
    public async Task<ActionResult<VoteDetailDto>> Get([FromQuery] string id, CancellationToken ct)
    {
        var v = await db.Votes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound($"Vote {id}");
        var rows = await (from cv in db.CountryVotes.AsNoTracking()
                          join m in db.MemberStates on cv.CountryCode equals m.Code
                          join i in db.Imports on cv.SourceImportId equals i.Id into imp
                          from i in imp.DefaultIfEmpty()
                          where cv.VoteId == id
                          orderby cv.CountryCode
                          select new CountryVoteDto(cv.CountryCode, m.Name, cv.Ballot.ToString(), cv.SourceConflictId != null,
                              cv.ApprovedBy, cv.ApprovedAt, cv.SourceImportId, i != null ? i.FileName : null)).ToListAsync(ct);
        var pending = await db.Conflicts.CountAsync(c => c.VoteId == id && c.Status == ConflictStatus.Pending, ct);
        var src = v.DetailsSourceImportId is null ? null : await db.Imports.AsNoTracking().Where(i => i.Id == v.DetailsSourceImportId)
            .Select(i => new SourceDto(i.Id, i.FileName, i.UploadedBy, i.UploadedAt, i.PublishedAt, null, null)).SingleOrDefaultAsync(ct);
        var tallies = new TalliesDto(rows.Count(r => r.Ballot == "Y"), rows.Count(r => r.Ballot == "N"), rows.Count(r => r.Ballot == "A"), rows.Count(r => r.Ballot == "X"));
        return new VoteDetailDto(v.Id, v.Session, v.VoteDate, v.Title, v.Position?.ToString(), v.Weight, v.PositionUpdatedBy, v.PositionUpdatedAt,
            tallies, pending, src, rows);
    }

    /// <summary>Everything that happened to a vote, newest first (HIS-04).</summary>
    [HttpGet("timeline")]
    public async Task<ActionResult<List<TimelineEventDto>>> Timeline([FromQuery] string id, CancellationToken ct)
    {
        if (!await db.Votes.AnyAsync(v => v.Id == id, ct)) throw ApiException.NotFound($"Vote {id}");
        return await (from e in db.VoteEvents.AsNoTracking()
                      join i in db.Imports on e.ImportId equals i.Id into imp
                      from i in imp.DefaultIfEmpty()
                      where e.VoteId == id
                      orderby e.At descending, e.Id descending
                      select new TimelineEventDto(e.Id, e.At, e.Actor, e.Type, e.CountryCode, e.Field, e.OldValue, e.NewValue,
                          e.ImportId, i != null ? i.FileName : null, e.ConflictId, e.SnapshotId, e.Message)).ToListAsync(ct);
    }

    /// <summary>Admin only (POS-01). Validation errors come back per field, e.g. weight 7 (POS-02).</summary>
    [HttpPut("position")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetPosition([FromQuery] string id, [FromBody] SetPositionRequest request, CancellationToken ct)
    {
        await service.SetPositionAsync(id, request.Position![0], request.Weight!.Value, User.UserId(), ct);
        return NoContent();
    }

    /// <summary>Admin only: apply one position/weight to all votes matching a filter.</summary>
    [HttpPost("positions/bulk")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<BulkPositionResultDto>> BulkSetPosition([FromBody] BulkPositionRequest request, CancellationToken ct)
    {
        var q = Filter(db.Votes, request.Session, request.From, request.To, null);
        if (request.OnlyWithoutPosition) q = q.Where(v => v.Position == null);
        var n = await service.SetPositionsAsync(q, request.Position![0], request.Weight!.Value, User.UserId(), ct);
        return new BulkPositionResultDto(n);
    }

    public static IQueryable<Vote> Filter(IQueryable<Vote> q, int? session, DateOnly? from, DateOnly? to, string? search)
    {
        if (session is not null) q = q.Where(v => v.Session == session);
        if (from is not null) q = q.Where(v => v.VoteDate >= from);
        if (to is not null) q = q.Where(v => v.VoteDate <= to);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(v => EF.Functions.ILike(v.Id, s) || EF.Functions.ILike(v.Title, s));
        }
        return q;
    }

    public static IQueryable<VoteListItemDto> Project(MeridianDbContext db, IQueryable<Vote> q) =>
        q.Select(v => new VoteListItemDto(v.Id, v.Session, v.VoteDate, v.Title,
            db.CountryVotes.Count(c => c.VoteId == v.Id),
            v.Position == null ? null : v.Position.ToString(),
            v.Weight,
            db.Conflicts.Count(c => c.VoteId == v.Id && c.Status == ConflictStatus.Pending)));
}
