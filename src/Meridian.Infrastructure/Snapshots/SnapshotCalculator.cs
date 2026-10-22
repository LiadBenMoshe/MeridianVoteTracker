using Meridian.Core.Domain;
using Meridian.Core.Scoring;
using Meridian.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Meridian.Infrastructure.Snapshots;

/// <summary>
/// Background calculation of a snapshot (SNP-02/03). Inputs are frozen into snapshot_* tables first,
/// so later data changes never affect a saved snapshot (SNP-04). Those tables are insert-only (DB trigger).
/// </summary>
public sealed class SnapshotCalculator(MeridianDbContext db, NpgsqlDataSource dataSource, ILogger<SnapshotCalculator> log)
{
    public async Task CalculateAsync(long snapshotId, CancellationToken ct)
    {
        var snapshot = await db.Snapshots.SingleAsync(s => s.Id == snapshotId, ct);
        Correlation.Id = snapshot.CorrelationId;
        using var scope = log.BeginScope("CorrelationId:{CorrelationId} SnapshotId:{SnapshotId}", snapshot.CorrelationId, snapshotId);
        try
        {
            await Progress(snapshot, 5, "Freezing approved ballots", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO snapshot_ballots (snapshot_id, vote_id, country_code, ballot)
                SELECT {snapshotId}, cv.vote_id, cv.country_code, cv.ballot
                FROM country_votes cv
                JOIN snapshot_votes sv ON sv.vote_id = cv.vote_id AND sv.snapshot_id = {snapshotId}
                ON CONFLICT DO NOTHING
                """, ct);

            await Progress(snapshot, 40, "Scoring", ct);
            if (!await db.SnapshotScores.AnyAsync(s => s.SnapshotId == snapshotId, ct))
                await ScoreAsync(snapshot, ct);

            snapshot.Status = JobStatus.Completed;
            snapshot.Progress = 100;
            snapshot.Phase = null;
            snapshot.ComputedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Snapshot {SnapshotId} completed", snapshotId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Snapshot {SnapshotId} failed", snapshotId);
            db.ChangeTracker.Clear();
            await db.Snapshots.Where(s => s.Id == snapshotId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatus.Failed)
                .SetProperty(x => x.Error, ex.Message), CancellationToken.None);
        }
    }

    private async Task ScoreAsync(Snapshot snapshot, CancellationToken ct)
    {
        var total = await db.SnapshotBallots.CountAsync(b => b.SnapshotId == snapshot.Id, ct);
        var acc = new Dictionary<string, (int Matched, int Counted, int Votes)>(StringComparer.Ordinal);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            SELECT b.country_code, b.ballot, sv.position, sv.weight
            FROM snapshot_ballots b
            JOIN snapshot_votes sv ON sv.snapshot_id = b.snapshot_id AND sv.vote_id = b.vote_id
            WHERE b.snapshot_id = @id
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", snapshot.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var n = 0;
            var ballotsReadAt = 0;
            while (await reader.ReadAsync(ct))
            {
                var code = reader.GetString(0).Trim();
                var ballot = reader.GetString(1)[0];
                var vote = new AlignmentScoring.VoteInput("", reader.GetString(2)[0], reader.GetInt32(3));
                var c = AlignmentScoring.Contribute(vote, ballot);
                var cur = acc.GetValueOrDefault(code);
                acc[code] = (cur.Matched + (c.Matched ? c.Weight : 0), cur.Counted + (c.Counted ? c.Weight : 0), cur.Votes + (c.Counted ? 1 : 0));
                if (++n - ballotsReadAt >= 100_000)
                {
                    ballotsReadAt = n;
                    // Progress goes on a separate connection: this one is busy streaming.
                    await using var p = await dataSource.OpenConnectionAsync(ct);
                    await using var u = new NpgsqlCommand("UPDATE snapshots SET progress = @p, phase = @ph WHERE id = @id", p);
                    u.Parameters.AddWithValue("p", 40 + (int)(50.0 * n / Math.Max(total, 1)));
                    u.Parameters.AddWithValue("ph", $"Scoring ({n:N0} of {total:N0} country votes)");
                    u.Parameters.AddWithValue("id", snapshot.Id);
                    await u.ExecuteNonQueryAsync(ct);
                }
            }
        }

        await Progress(snapshot, 92, "Ranking", ct);
        var states = await db.MemberStates.AsNoTracking().OrderBy(m => m.Code).ToListAsync(ct);
        var scores = states.Select(m =>
        {
            var a = acc.GetValueOrDefault(m.Code);
            return new SnapshotScore
            {
                SnapshotId = snapshot.Id,
                CountryCode = m.Code,
                CountryName = m.Name,
                MatchedWeight = a.Matched,
                CountedWeight = a.Counted,
                CountedVotes = a.Votes,
                Score = a.Counted == 0 ? null : Math.Round(100.0 * a.Matched / a.Counted, 4),
            };
        }).ToList();

        foreach (var s in scores.Where(s => s.Score is not null))
            s.Rank = 1 + scores.Count(o => o.Score > s.Score);

        db.SnapshotScores.AddRange(scores);
        await db.SaveChangesAsync(ct);
    }

    private async Task Progress(Snapshot s, int progress, string phase, CancellationToken ct)
    {
        s.Progress = progress;
        s.Phase = phase;
        await db.SaveChangesAsync(ct);
    }
}
