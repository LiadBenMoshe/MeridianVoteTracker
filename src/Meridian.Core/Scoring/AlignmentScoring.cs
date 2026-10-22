using Meridian.Core.Domain;

namespace Meridian.Core.Scoring;

/// <summary>
/// Alignment Index rules (section 7.6). The open points are decided here and shown in every snapshot.
/// The same rules are used by the SQL calculation; this class is the readable reference + unit-test target.
/// </summary>
public static class AlignmentScoring
{
    public const string Version = "v1";

    public static readonly string[] Rules =
    [
        "Score = 100 × (sum of weights where the ballot matched the institute position) ÷ (sum of weights of counted votes).",
        "Only YES and NO ballots are counted.",
        "ABSTAIN is not counted (neither for nor against).",
        "ABSENT is not counted.",
        "Votes held while the state was not a member are excluded (no ballot is stored for them).",
        "A state with no counted vote gets no score (n/a) and no rank.",
        "Every vote in a snapshot must have a position and a weight; votes without a position cannot be included.",
        "Equal scores share the same rank. Scores are shown with one decimal.",
    ];

    public sealed record VoteInput(string VoteId, char Position, int Weight);

    public sealed record Contribution(string VoteId, char Position, int Weight, char? Ballot, bool Counted, bool Matched, string Note);

    public sealed record CountryScore(double? Score, int Matched, int Counted, IReadOnlyList<Contribution> Contributions);

    public static Contribution Contribute(VoteInput vote, char? ballot)
    {
        if (ballot is null) return new(vote.VoteId, vote.Position, vote.Weight, null, false, false, "No ballot (not a member at the time)");
        return ballot switch
        {
            Ballots.Yes or Ballots.No when ballot == vote.Position => new(vote.VoteId, vote.Position, vote.Weight, ballot, true, true, "Matched"),
            Ballots.Yes or Ballots.No => new(vote.VoteId, vote.Position, vote.Weight, ballot, true, false, "Did not match"),
            Ballots.Abstain => new(vote.VoteId, vote.Position, vote.Weight, ballot, false, false, "Abstain: not counted"),
            _ => new(vote.VoteId, vote.Position, vote.Weight, ballot, false, false, "Absent: not counted"),
        };
    }

    public static CountryScore Score(IEnumerable<VoteInput> votes, IReadOnlyDictionary<string, char> ballotsByVote)
    {
        var contributions = votes
            .Select(v => Contribute(v, ballotsByVote.TryGetValue(v.VoteId, out var b) ? b : null))
            .ToList();
        var counted = contributions.Where(c => c.Counted).Sum(c => c.Weight);
        var matched = contributions.Where(c => c.Matched).Sum(c => c.Weight);
        double? score = counted == 0 ? null : 100.0 * matched / counted;
        return new CountryScore(score, matched, counted, contributions);
    }
}
