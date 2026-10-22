using Meridian.Api.Contracts;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Services;

public sealed class VoteService(MeridianDbContext db, ILogger<VoteService> log)
{
    /// <summary>POS-01/03: set position + weight; every change is logged with old and new value.</summary>
    public async Task SetPositionAsync(string voteId, char position, int weight, string userId, CancellationToken ct)
    {
        var vote = await db.Votes.SingleOrDefaultAsync(v => v.Id == voteId, ct) ?? throw ApiException.NotFound($"Vote {voteId}");
        Apply(vote, position, weight, userId, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Same as above for many votes at once (e.g. to prepare a full-history snapshot). Each vote gets its own audit event.</summary>
    public async Task<int> SetPositionsAsync(IQueryable<Vote> votes, char position, int weight, string userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var list = await votes.ToListAsync(ct);
        foreach (var v in list) Apply(v, position, weight, userId, now);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Bulk position {Position}/{Weight} applied to {Count} votes by {User}", position, weight, list.Count, userId);
        return list.Count;
    }

    private void Apply(Vote vote, char position, int weight, string userId, DateTimeOffset now)
    {
        if (vote.Position == position && vote.Weight == weight) return;
        if (vote.Position != position)
            db.VoteEvents.Add(new VoteEvent
            {
                VoteId = vote.Id, At = now, Actor = userId, Type = VoteEventTypes.PositionChanged, Field = "position",
                OldValue = vote.Position?.ToString(), NewValue = position.ToString(),
                Message = $"Desired outcome {Describe(vote.Position)} → {Describe(position)}",
            });
        if (vote.Weight != weight)
            db.VoteEvents.Add(new VoteEvent
            {
                VoteId = vote.Id, At = now, Actor = userId, Type = VoteEventTypes.PositionChanged, Field = "weight",
                OldValue = vote.Weight?.ToString(), NewValue = weight.ToString(),
                Message = $"Weight {vote.Weight?.ToString() ?? "none"} → {weight}",
            });
        vote.Position = position;
        vote.Weight = weight;
        vote.PositionUpdatedAt = now;
        vote.PositionUpdatedBy = userId;
    }

    private static string Describe(char? p) => p switch { 'Y' => "YES", 'N' => "NO", _ => "none" };
}
