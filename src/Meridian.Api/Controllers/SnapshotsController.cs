using System.Text.Json;
using Meridian.Api.Contracts;
using Meridian.Api.Infrastructure;
using Meridian.Api.Services;
using Meridian.Core.Scoring;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Controllers;

[ApiController]
[Route("api/snapshots")]
[Authorize(Policy = Policies.Analyst)]
public sealed class SnapshotsController(MeridianDbContext db, SnapshotService service) : ControllerBase
{
    /// <summary>How many votes a selection matches, and which have no position (shown before creating).</summary>
    [HttpPost("preview")]
    public async Task<ActionResult<SnapshotPreviewDto>> Preview([FromBody] SnapshotSelection selection, CancellationToken ct) =>
        await service.PreviewAsync(selection, ct);

    /// <summary>Create a snapshot. Returns 202; the calculation runs in the background (SNP-02). 422 if a vote has no position (POS-04).</summary>
    [HttpPost]
    [ProducesResponseType<SnapshotDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SnapshotDto>> Create([FromBody] CreateSnapshotRequest request, CancellationToken ct)
    {
        var s = await service.CreateAsync(request, User.UserId(), ct);
        return AcceptedAtAction(nameof(Get), new { id = s.Id }, await ToDtoAsync(s, ct));
    }

    [HttpGet]
    public async Task<ActionResult<List<SnapshotDto>>> List(CancellationToken ct)
    {
        var list = await db.Snapshots.AsNoTracking().OrderByDescending(s => s.Id).Take(200).ToListAsync(ct);
        var result = new List<SnapshotDto>();
        foreach (var s in list) result.Add(await ToDtoAsync(s, ct));
        return result;
    }

    /// <summary>Score and rank per country (SNP-03). Read from frozen tables, so it never changes (SNP-04).</summary>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<SnapshotRankingDto>> Get(long id, CancellationToken ct)
    {
        var s = await db.Snapshots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound($"Snapshot {id}");
        var scores = await db.SnapshotScores.AsNoTracking().Where(x => x.SnapshotId == id)
            .OrderBy(x => x.Rank == null).ThenBy(x => x.Rank).ThenBy(x => x.CountryName)
            .Select(x => new SnapshotScoreDto(x.CountryCode, x.CountryName, x.Score, x.Rank, x.MatchedWeight, x.CountedWeight, x.CountedVotes))
            .ToListAsync(ct);
        return new SnapshotRankingDto(await ToDtoAsync(s, ct), scores.Count(x => x.Rank != null), scores);
    }

    /// <summary>Per-country breakdown: the contribution of every vote, with the rules applied (SNP-03).</summary>
    [HttpGet("{id:long}/countries/{code}")]
    public async Task<ActionResult<CountryBreakdownDto>> Country(long id, string code, CancellationToken ct)
    {
        code = code.Trim().ToUpperInvariant();
        var s = await db.Snapshots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound($"Snapshot {id}");
        var score = await db.SnapshotScores.AsNoTracking().SingleOrDefaultAsync(x => x.SnapshotId == id && x.CountryCode == code, ct)
                    ?? throw ApiException.NotFound($"Country {code} in snapshot {id}");
        var ranked = await db.SnapshotScores.CountAsync(x => x.SnapshotId == id && x.Rank != null, ct);

        var rows = await (from sv in db.SnapshotVotes.AsNoTracking()
                          where sv.SnapshotId == id
                          join b in db.SnapshotBallots.Where(b => b.SnapshotId == id && b.CountryCode == code) on sv.VoteId equals b.VoteId into bs
                          from b in bs.DefaultIfEmpty()
                          join cv in db.CountryVotes.Where(c => c.CountryCode == code) on sv.VoteId equals cv.VoteId into cvs
                          from cv in cvs.DefaultIfEmpty()
                          orderby sv.VoteDate, sv.VoteId
                          select new { sv.VoteId, sv.Title, sv.VoteDate, sv.Position, sv.Weight, Ballot = (char?)b.Ballot, Current = (char?)cv.Ballot })
            .ToListAsync(ct);

        var contributions = rows.Select(r =>
        {
            var c = AlignmentScoring.Contribute(new AlignmentScoring.VoteInput(r.VoteId, r.Position, r.Weight), r.Ballot);
            return new ContributionDto(r.VoteId, r.Title, r.VoteDate, r.Position.ToString(), r.Weight, r.Ballot?.ToString(),
                c.Counted, c.Matched, c.Note, r.Current?.ToString());
        }).ToList();

        return new CountryBreakdownDto(
            new SnapshotScoreDto(score.CountryCode, score.CountryName, score.Score, score.Rank, score.MatchedWeight, score.CountedWeight, score.CountedVotes),
            ranked,
            JsonSerializer.Deserialize<List<string>>(s.RulesJson) ?? [],
            contributions,
            contributions.Count(c => c.Ballot != c.CurrentBallot));
    }

    /// <summary>Admin only: mark as the published snapshot of a quarter (SNP-05).</summary>
    [HttpPost("{id:long}/publish")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<SnapshotDto>> Publish(long id, [FromBody] PublishRequest request, CancellationToken ct)
    {
        await service.PublishAsync(id, request.Quarter!, User.UserId(), ct);
        var s = await db.Snapshots.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        return await ToDtoAsync(s, ct);
    }

    private async Task<SnapshotDto> ToDtoAsync(Snapshot s, CancellationToken ct)
    {
        var range = await db.SnapshotVotes.AsNoTracking().Where(v => v.SnapshotId == s.Id)
            .GroupBy(_ => 1).Select(g => new { Min = g.Min(v => v.VoteDate), Max = g.Max(v => v.VoteDate) }).FirstOrDefaultAsync(ct);
        string? suggested = null;
        if (range is not null) suggested = $"{range.Max.Year}-Q{(range.Max.Month - 1) / 3 + 1}";

        SnapshotSelection? sel = null;
        try { sel = JsonSerializer.Deserialize<SnapshotSelection>(s.SelectionJson); } catch (JsonException) { }

        return new SnapshotDto(s.Id, s.Name, s.CreatedBy, s.CreatedAt, s.Status, s.Progress, s.Phase, s.VoteCount, s.ComputedAt, s.Error,
            s.PublishedQuarter, s.PublishedBy, s.PublishedAt, SnapshotService.Describe(sel),
            JsonSerializer.Deserialize<List<string>>(s.RulesJson) ?? [], s.RulesVersion, suggested);
    }
}
