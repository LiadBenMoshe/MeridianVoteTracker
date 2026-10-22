using Meridian.Api.Contracts;
using Meridian.Core.Domain;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Services;

public sealed class ConflictService(MeridianDbContext db, ILogger<ConflictService> log)
{
    /// <summary>
    /// CON-04/05: the decision is a single conditional UPDATE (… WHERE status = 'Pending').
    /// If two analysts decide at the same moment, exactly one row update succeeds; the other gets 409
    /// with the current state. Accepting writes the new approved value and an audit event in the same transaction.
    /// </summary>
    public async Task<ConflictDto> DecideAsync(long id, Decision decision, string? comment, string userId, CancellationToken ct)
    {
        var conflict = await db.Conflicts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct)
                       ?? throw ApiException.NotFound($"Conflict {id}");
        var status = decision == Decision.Accept ? ConflictStatus.Accepted : ConflictStatus.Rejected;
        var now = DateTimeOffset.UtcNow;
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await CurrentApprovedValueAsync(conflict, lockRow: true, ct);

        var won = await db.Conflicts
            .Where(c => c.Id == id && c.Status == ConflictStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, status)
                .SetProperty(c => c.DecidedBy, userId)
                .SetProperty(c => c.DecidedAt, now)
                .SetProperty(c => c.Comment, comment)
                .SetProperty(c => c.ValueBeforeDecision, current), ct);

        if (won == 0)
        {
            await tx.RollbackAsync(ct);
            await NoteLostDecisionAsync(conflict, decision, userId, ct);
            var latest = await db.Conflicts.AsNoTracking().SingleAsync(c => c.Id == id, ct);
            var dto = (await ConflictMapper.ToDtosAsync(db, [latest], ct))[0];
            var verb = latest.Status == ConflictStatus.Accepted ? "accepted" : "rejected";
            throw new ApiException(409, "Already decided",
                $"{Name(latest.DecidedBy)} {verb} this conflict at {latest.DecidedAt:yyyy-MM-dd HH:mm:ss} UTC. Your decision ({decision}) was not applied.",
                new Dictionary<string, object?> { ["current"] = dto });
        }

        if (status == ConflictStatus.Accepted)
        {
            if (conflict.Kind == ConflictKind.CountryVote)
            {
                await db.CountryVotes
                    .Where(c => c.VoteId == conflict.VoteId && c.CountryCode == conflict.CountryCode)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Ballot, conflict.IncomingValue[0])
                        .SetProperty(c => c.SourceImportId, conflict.IncomingImportId)
                        .SetProperty(c => c.SourceConflictId, conflict.Id)
                        .SetProperty(c => c.ApprovedAt, now)
                        .SetProperty(c => c.ApprovedBy, userId), ct);
            }
            else
            {
                var vote = await db.Votes.SingleAsync(v => v.Id == conflict.VoteId, ct);
                switch (conflict.Field)
                {
                    case VoteFields.Title: vote.Title = conflict.IncomingValue; break;
                    case VoteFields.Date: vote.VoteDate = DateOnly.Parse(conflict.IncomingValue); break;
                    case VoteFields.Session: vote.Session = int.Parse(conflict.IncomingValue); break;
                }
                vote.DetailsSourceImportId = conflict.IncomingImportId;
                await db.SaveChangesAsync(ct);
            }
        }

        var importFile = await db.Imports.Where(i => i.Id == conflict.IncomingImportId).Select(i => i.FileName).SingleAsync(ct);
        db.VoteEvents.Add(new VoteEvent
        {
            VoteId = conflict.VoteId,
            At = now,
            Actor = userId,
            Type = status == ConflictStatus.Accepted ? VoteEventTypes.ConflictAccepted : VoteEventTypes.ConflictRejected,
            CountryCode = conflict.CountryCode == "" ? null : conflict.CountryCode,
            Field = conflict.Field,
            OldValue = current,
            NewValue = conflict.IncomingValue,
            ImportId = conflict.IncomingImportId,
            ConflictId = conflict.Id,
            Message = (status == ConflictStatus.Accepted
                          ? $"Accepted value from {importFile}"
                          : $"Rejected value from {importFile}; approved value kept") +
                      (comment is null ? "" : $". Comment: {comment}"),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        log.LogInformation("Conflict {ConflictId} {Status} by {User}", id, status, userId);

        var saved = await db.Conflicts.AsNoTracking().SingleAsync(c => c.Id == id, ct);
        return (await ConflictMapper.ToDtosAsync(db, [saved], ct))[0];
    }

    private async Task<string> CurrentApprovedValueAsync(Conflict c, bool lockRow, CancellationToken ct)
    {
        if (c.Kind == ConflictKind.CountryVote)
        {
            var sql = lockRow
                ? "SELECT ballot::text AS \"Value\" FROM country_votes WHERE vote_id = {0} AND country_code = {1} FOR UPDATE"
                : "SELECT ballot::text AS \"Value\" FROM country_votes WHERE vote_id = {0} AND country_code = {1}";
            return (await db.Database.SqlQueryRaw<string>(sql, c.VoteId, c.CountryCode).ToListAsync(ct)).Single();
        }
        var v = await db.Votes.AsNoTracking().SingleAsync(x => x.Id == c.VoteId, ct);
        return ConflictMapper.FieldValue(v, c.Field);
    }

    /// <summary>The losing decision is not applied, but it is recorded in the vote timeline.</summary>
    private async Task NoteLostDecisionAsync(Conflict c, Decision decision, string userId, CancellationToken ct)
    {
        db.VoteEvents.Add(new VoteEvent
        {
            VoteId = c.VoteId, At = DateTimeOffset.UtcNow, Actor = userId, Type = VoteEventTypes.DecisionNotApplied,
            CountryCode = c.CountryCode == "" ? null : c.CountryCode, Field = c.Field, ConflictId = c.Id,
            Message = $"{decision} not applied: conflict was already decided",
        });
        await db.SaveChangesAsync(ct);
    }

    private static string Name(string? userId) => userId is null ? "Someone" : char.ToUpperInvariant(userId[0]) + userId[1..];
}

public static class ConflictMapper
{
    public static string FieldValue(Vote v, string field) => field switch
    {
        VoteFields.Title => v.Title,
        VoteFields.Date => v.VoteDate.ToString("yyyy-MM-dd"),
        VoteFields.Session => v.Session.ToString(),
        _ => "",
    };

    public static async Task<List<ConflictDto>> ToDtosAsync(MeridianDbContext db, IReadOnlyList<Conflict> conflicts, CancellationToken ct)
    {
        if (conflicts.Count == 0) return new();
        var voteIds = conflicts.Select(c => c.VoteId).Distinct().ToList();
        var votes = await db.Votes.AsNoTracking().Where(v => voteIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var codes = conflicts.Select(c => c.CountryCode).Where(c => c != "").Distinct().ToList();
        var names = await db.MemberStates.AsNoTracking().Where(m => codes.Contains(m.Code)).ToDictionaryAsync(m => m.Code, m => m.Name, ct);
        var cells = await db.CountryVotes.AsNoTracking().Where(c => voteIds.Contains(c.VoteId) && codes.Contains(c.CountryCode)).ToListAsync(ct);
        var cellMap = cells.ToDictionary(c => (c.VoteId, c.CountryCode));
        var importIds = conflicts.SelectMany(c => new[] { c.IncomingImportId, c.ApprovedSourceImportId ?? 0 }).Distinct().ToList();
        var imports = await db.Imports.AsNoTracking().Where(i => importIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var published = await db.Snapshots.AsNoTracking()
            .Where(s => s.PublishedQuarter != null)
            .Join(db.SnapshotVotes.Where(sv => voteIds.Contains(sv.VoteId)), s => s.Id, sv => sv.SnapshotId, (s, sv) => new { s.Id, s.Name, s.PublishedQuarter, sv.VoteId })
            .ToListAsync(ct);
        var publishedIds = published.Select(p => p.Id).Distinct().ToList();
        var publishedBallots = await db.SnapshotBallots.AsNoTracking()
            .Where(b => voteIds.Contains(b.VoteId) && publishedIds.Contains(b.SnapshotId) && codes.Contains(b.CountryCode))
            .ToListAsync(ct);

        SourceDto? Source(long? importId, string? approvedBy, DateTimeOffset? approvedAt) =>
            importId is not null && imports.TryGetValue(importId.Value, out var i)
                ? new SourceDto(i.Id, i.FileName, i.UploadedBy, i.UploadedAt, i.PublishedAt, approvedBy, approvedAt)
                : approvedBy is null ? null : new SourceDto(null, null, null, null, null, approvedBy, approvedAt);

        return conflicts.Select(c =>
        {
            var v = votes[c.VoteId];
            cellMap.TryGetValue((c.VoteId, c.CountryCode), out var cell);
            var current = c.Kind == ConflictKind.CountryVote ? cell?.Ballot.ToString() ?? "" : FieldValue(v, c.Field);
            var impact = new List<string>();
            foreach (var p in published.Where(p => p.VoteId == c.VoteId))
            {
                var used = c.Kind == ConflictKind.CountryVote
                    ? publishedBallots.FirstOrDefault(b => b.SnapshotId == p.Id && b.CountryCode.Trim() == c.CountryCode)?.Ballot.ToString()
                    : null;
                impact.Add($"Published snapshot \"{p.Name}\" ({p.PublishedQuarter}) will not change" +
                           (used is null ? "." : $": it keeps {Ballots.Describe(used[0])}."));
            }
            impact.Add("Snapshots created from now on use the value approved here.");

            return new ConflictDto(
                c.Id, c.Kind, c.VoteId, v.Title, v.VoteDate, v.Session,
                c.CountryCode == "" ? null : c.CountryCode,
                c.CountryCode == "" ? null : names.GetValueOrDefault(c.CountryCode),
                c.Field, c.ApprovedValue, c.IncomingValue, current,
                Source(c.ApprovedSourceImportId, c.Kind == ConflictKind.CountryVote ? cell?.ApprovedBy : null, c.Kind == ConflictKind.CountryVote ? cell?.ApprovedAt : null),
                Source(c.IncomingImportId, null, null)!,
                c.Status, c.CreatedAt, c.DecidedBy, c.DecidedAt, c.Comment, c.ValueBeforeDecision, impact);
        }).ToList();
    }
}
