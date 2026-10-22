using System.Text.Json;
using Meridian.Api.Contracts;
using Meridian.Api.Controllers;
using Meridian.Api.Infrastructure;
using Meridian.Core.Domain;
using Meridian.Core.Scoring;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Meridian.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Services;

public sealed class SnapshotService(MeridianDbContext db, JobSignal signal, ILogger<SnapshotService> log)
{
    private sealed record Resolved(List<string> VoteIds, List<string> Unknown);

    /// <summary>SNP-01: votes matched by the filter (date range / session) plus any listed votes.</summary>
    private async Task<Resolved> ResolveAsync(SnapshotSelection sel, CancellationToken ct)
    {
        var hasFilter = sel.From is not null || sel.To is not null || sel.Session is not null;
        var listed = (sel.VoteIds ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList();
        if (!hasFilter && listed.Count == 0)
            throw FieldValidationException.For("Selection", "Choose a date range, a session or a list of votes.");
        if (sel.From is not null && sel.To is not null && sel.From > sel.To)
            throw FieldValidationException.For("Selection.To", "'To' must be on or after 'From'.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (hasFilter)
            ids.UnionWith(await VotesController.Filter(db.Votes.AsNoTracking(), sel.Session, sel.From, sel.To, null).Select(v => v.Id).ToListAsync(ct));
        var known = await db.Votes.AsNoTracking().Where(v => listed.Contains(v.Id)).Select(v => v.Id).ToListAsync(ct);
        ids.UnionWith(known);
        return new Resolved(ids.ToList(), listed.Except(known).ToList());
    }

    public async Task<SnapshotPreviewDto> PreviewAsync(SnapshotSelection sel, CancellationToken ct)
    {
        var r = await ResolveAsync(sel, ct);
        var missing = db.Votes.AsNoTracking().Where(v => r.VoteIds.Contains(v.Id) && v.Position == null);
        var missingTotal = await missing.CountAsync(ct);
        var sample = await VotesController.Project(db, missing.OrderBy(v => v.VoteDate).ThenBy(v => v.Id).Take(50)).ToListAsync(ct);
        return new SnapshotPreviewDto(r.VoteIds.Count, missingTotal, sample, r.Unknown);
    }

    /// <summary>
    /// Freezes the selected votes with their position/weight right now, then queues the calculation (SNP-02).
    /// A vote without a position blocks creation instead of being silently skipped (POS-04).
    /// </summary>
    public async Task<Snapshot> CreateAsync(CreateSnapshotRequest req, string userId, CancellationToken ct)
    {
        var sel = req.Selection!;
        var r = await ResolveAsync(sel, ct);
        if (r.Unknown.Count > 0)
            throw FieldValidationException.For("Selection.VoteIds", $"Unknown vote id(s): {string.Join(", ", r.Unknown.Take(20))}");
        if (r.VoteIds.Count == 0)
            throw FieldValidationException.For("Selection", "No votes match this selection.");

        var missing = await db.Votes.AsNoTracking().Where(v => r.VoteIds.Contains(v.Id) && v.Position == null)
            .OrderBy(v => v.Id).Select(v => v.Id).ToListAsync(ct);
        if (missing.Count > 0)
            throw new ApiException(422, "Votes without a position",
                $"{missing.Count} selected vote(s) have no institute position. Set a position or remove them from the selection.",
                new Dictionary<string, object?> { ["missingPositions"] = missing.Take(200).ToList(), ["missingPositionTotal"] = missing.Count });

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var snapshot = new Snapshot
        {
            Name = req.Name!.Trim(),
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = JobStatus.Queued,
            Phase = "Waiting in queue",
            SelectionJson = JsonSerializer.Serialize(sel),
            RulesJson = JsonSerializer.Serialize(AlignmentScoring.Rules),
            RulesVersion = AlignmentScoring.Version,
            VoteCount = r.VoteIds.Count,
            CorrelationId = Correlation.Id,
        };
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);

        var ids = r.VoteIds.ToArray();
        var copied = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO snapshot_votes (snapshot_id, vote_id, title, vote_date, position, weight)
            SELECT {snapshot.Id}, v.id, v.title, v.vote_date, v.position, v.weight
            FROM votes v WHERE v.id = ANY({ids}) AND v.position IS NOT NULL
            """, ct);
        if (copied != ids.Length)
            throw new ApiException(409, "Selection changed", "Some votes changed while the snapshot was being created. Try again.");
        await tx.CommitAsync(ct);

        signal.Notify(Queues.Snapshots);
        log.LogInformation("Snapshot {SnapshotId} queued with {Votes} votes", snapshot.Id, ids.Length);
        return snapshot;
    }

    /// <summary>SNP-05: admin marks one snapshot per quarter as published (unique index guarantees one).</summary>
    public async Task PublishAsync(long id, string quarter, string userId, CancellationToken ct)
    {
        var s = await db.Snapshots.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound($"Snapshot {id}");
        if (s.Status != JobStatus.Completed) throw new ApiException(409, "Not ready", "Only a completed snapshot can be published.");
        if (s.PublishedQuarter is not null) throw new ApiException(409, "Already published", $"This snapshot is already published for {s.PublishedQuarter}.");

        var existing = await db.Snapshots.AsNoTracking().Where(x => x.PublishedQuarter == quarter).Select(x => new { x.Id, x.Name }).FirstOrDefaultAsync(ct);
        if (existing is not null)
            throw new ApiException(409, "Quarter already published", $"{quarter} already has a published snapshot: \"{existing.Name}\" (#{existing.Id}).",
                new Dictionary<string, object?> { ["publishedSnapshotId"] = existing.Id });

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        s.PublishedQuarter = quarter;
        s.PublishedBy = userId;
        s.PublishedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ApiExceptionHandler.IsUniqueViolation(ex))
        {
            throw new ApiException(409, "Quarter already published", $"{quarter} was just published by someone else.");
        }

        var msg = $"Snapshot \"{s.Name}\" published as {quarter}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO vote_events (vote_id, at, actor, type, snapshot_id, message)
            SELECT sv.vote_id, now(), {userId}, {VoteEventTypes.SnapshotPublished}, {id}, {msg}
            FROM snapshot_votes sv WHERE sv.snapshot_id = {id}
            """, ct);
        await tx.CommitAsync(ct);
    }

    public static string Describe(SnapshotSelection? sel)
    {
        if (sel is null) return "";
        var parts = new List<string>();
        if (sel.From is not null || sel.To is not null) parts.Add($"{sel.From?.ToString("yyyy-MM-dd") ?? "…"} – {sel.To?.ToString("yyyy-MM-dd") ?? "…"}");
        if (sel.Session is not null) parts.Add($"session {sel.Session}");
        if (sel.VoteIds is { Count: > 0 }) parts.Add($"{sel.VoteIds.Count} listed vote(s)");
        return string.Join(" + ", parts);
    }
}
