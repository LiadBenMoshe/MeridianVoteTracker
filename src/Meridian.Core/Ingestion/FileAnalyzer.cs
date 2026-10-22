using Meridian.Core.Domain;

namespace Meridian.Core.Ingestion;

/// <summary>Final classification of one row. Raw text is kept for rows that were not stored.</summary>
public sealed record RowResult(int RowNo, string? VoteId, string? CountryCode, string? Ballot, RowOutcome Outcome, string? Reason, string? Raw);

public sealed record CandidateBallot(int RowNo, char Ballot);

/// <summary>Everything one file says about one vote, after in-file checks (ING-03, ING-05).</summary>
public sealed class CandidateVote(string voteId)
{
    public string VoteId { get; } = voteId;
    /// <summary>Null when the rows disagree on the vote details (contradiction).</summary>
    public VoteDetails? Details { get; set; }
    public string? DetailsContradiction { get; set; }
    public Dictionary<string, CandidateBallot> Ballots { get; } = new(StringComparer.Ordinal);
    public int TotalRows { get; set; }
    public int Duplicates { get; set; }
    public int Invalid { get; set; }
    public int Contradictions { get; set; }
    public List<string> ContradictedCountries { get; } = new();
}

public sealed class FileAnalysis
{
    /// <summary>Rows already decided by in-file checks: Invalid, Duplicate, Contradiction.</summary>
    public List<RowResult> Rejected { get; } = new();
    /// <summary>Votes in first-seen order.</summary>
    public List<CandidateVote> Votes { get; } = new();
    public List<NormalizationNote> Notes { get; } = new();
    public int TotalRows { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>
/// Pass 1: validate each row, group by vote id (rows need not be contiguous), then apply in-file rules:
/// exact duplicates, contradicting ballots for the same country, contradicting vote details.
/// No database access here, so it is fully unit-testable.
/// </summary>
public static class FileAnalyzer
{
    public static FileAnalysis Analyze(IEnumerable<RawRow> rows, MemberRegistry members, Action<int>? onProgress = null)
    {
        var validator = new RowValidator(members);
        var analysis = new FileAnalysis();
        var byVote = new Dictionary<string, List<ValidRow>>(StringComparer.Ordinal);
        var invalidByVote = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var raw in rows)
        {
            analysis.TotalRows++;
            if (onProgress is not null && analysis.TotalRows % 20000 == 0) onProgress(analysis.TotalRows);

            var result = validator.Validate(raw);
            analysis.Notes.AddRange(result.Notes);
            if (result.Row is null)
            {
                analysis.Rejected.Add(new RowResult(raw.RowNo, result.VoteId, result.CountryCode, result.BallotText,
                    RowOutcome.Invalid, result.Reason, raw.Raw));
                if (result.VoteId is not null)
                {
                    invalidByVote[result.VoteId] = invalidByVote.GetValueOrDefault(result.VoteId) + 1;
                    if (!byVote.ContainsKey(result.VoteId))
                    {
                        byVote[result.VoteId] = new();
                        order.Add(result.VoteId);
                    }
                }
                continue;
            }

            var row = result.Row;
            if (analysis.PublishedAt is null || row.PublishedAt > analysis.PublishedAt) analysis.PublishedAt = row.PublishedAt;
            if (!byVote.TryGetValue(row.VoteId, out var list))
            {
                byVote[row.VoteId] = list = new();
                order.Add(row.VoteId);
            }
            list.Add(row);
        }

        foreach (var voteId in order)
        {
            var list = byVote[voteId];
            var cand = new CandidateVote(voteId)
            {
                TotalRows = list.Count + invalidByVote.GetValueOrDefault(voteId),
                Invalid = invalidByVote.GetValueOrDefault(voteId),
            };
            analysis.Votes.Add(cand);
            if (list.Count == 0) continue;

            // ING-05: all rows of a vote must agree on session/date/title.
            var distinctDetails = list.Select(r => r.Details).Distinct().ToList();
            if (distinctDetails.Count > 1)
            {
                var reason = "Vote details differ between rows in this file: " + DescribeDifference(distinctDetails);
                cand.DetailsContradiction = reason;
                foreach (var r in list)
                    analysis.Rejected.Add(new RowResult(r.RowNo, voteId, r.CountryCode, r.Ballot.ToString(),
                        RowOutcome.Contradiction, reason, r.Raw));
                cand.Contradictions += list.Count;
                continue;
            }
            cand.Details = distinctDetails[0];

            // ING-05: one value per country per vote.
            foreach (var group in list.GroupBy(r => r.CountryCode, StringComparer.Ordinal))
            {
                var rowsForCountry = group.ToList();
                var distinctBallots = rowsForCountry.Select(r => r.Ballot).Distinct().ToList();
                if (distinctBallots.Count > 1)
                {
                    var first = rowsForCountry[0];
                    foreach (var r in rowsForCountry)
                    {
                        var reason = r == first
                            ? $"{group.Key} appears {rowsForCountry.Count} times in this vote with different values ({string.Join("/", distinctBallots)}). Neither value was stored"
                            : $"See row {first.RowNo}. Neither value was stored";
                        analysis.Rejected.Add(new RowResult(r.RowNo, voteId, r.CountryCode, r.Ballot.ToString(),
                            RowOutcome.Contradiction, reason, r.Raw));
                    }
                    cand.Contradictions += rowsForCountry.Count;
                    cand.ContradictedCountries.Add(group.Key);
                    continue;
                }

                var keep = rowsForCountry[0];
                cand.Ballots[group.Key] = new CandidateBallot(keep.RowNo, keep.Ballot);
                foreach (var dup in rowsForCountry.Skip(1))
                {
                    var reason = dup.Raw == keep.Raw
                        ? $"Identical to row {keep.RowNo}"
                        : $"Same vote, country and ballot as row {keep.RowNo} (other columns differ)";
                    analysis.Rejected.Add(new RowResult(dup.RowNo, voteId, dup.CountryCode, dup.Ballot.ToString(),
                        RowOutcome.Duplicate, reason, dup.Raw));
                    cand.Duplicates++;
                }
            }
        }

        return analysis;
    }

    private static string DescribeDifference(IReadOnlyList<VoteDetails> details)
    {
        var parts = new List<string>();
        if (details.Select(d => d.Session).Distinct().Count() > 1)
            parts.Add("session " + string.Join(" vs ", details.Select(d => d.Session).Distinct()));
        if (details.Select(d => d.Date).Distinct().Count() > 1)
            parts.Add("date " + string.Join(" vs ", details.Select(d => d.Date.ToString("yyyy-MM-dd")).Distinct()));
        if (details.Select(d => d.Title).Distinct().Count() > 1)
            parts.Add("title " + string.Join(" vs ", details.Select(d => $"\"{d.Title}\"").Distinct()));
        return string.Join("; ", parts);
    }
}
