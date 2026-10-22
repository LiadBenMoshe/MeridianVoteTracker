using Meridian.Core.Domain;

namespace Meridian.Core.Ingestion;

/// <summary>A conflict that already exists for a cell (pending or decided).</summary>
public sealed record KnownConflict(long Id, string CountryCode, string Field, string ApprovedValue, string IncomingValue, ConflictStatus Status);

/// <summary>What the database currently holds for a vote (approved data only).</summary>
public sealed class ExistingVote
{
    public required VoteDetails Details { get; init; }
    public Dictionary<string, char> Ballots { get; init; } = new(StringComparer.Ordinal);
    public List<KnownConflict> Conflicts { get; init; } = new();
}

public sealed record NewBallot(string CountryCode, char Ballot, int RowNo);

public sealed record ConflictToOpen(ConflictKind Kind, string CountryCode, string Field, string ApprovedValue, string IncomingValue);

public sealed class VoteReconciliation
{
    public required string VoteId { get; init; }
    public VoteDetails? CreateVote { get; set; }
    public List<NewBallot> NewBallots { get; } = new();
    public List<ConflictToOpen> NewConflicts { get; } = new();
    public List<RowResult> Rows { get; } = new();
    public int NewCount { get; set; }
    public int UnchangedCount { get; set; }
    public int ConflictRows { get; set; }
    public string Outcome { get; set; } = "";
    public string Details { get; set; } = "";
}

/// <summary>
/// Pass 2: compare one candidate vote with approved data (ING-06, ING-08..12).
/// Approved data is never changed here: differences only become conflicts.
/// </summary>
public static class VoteReconciler
{
    public static VoteReconciliation Reconcile(CandidateVote cand, ExistingVote? existing)
    {
        var rec = new VoteReconciliation { VoteId = cand.VoteId };
        var details = new List<string>();

        if (cand.Details is null)
        {
            rec.Outcome = cand.DetailsContradiction is not null ? "Contradiction" : "Not stored";
            details.Add(cand.DetailsContradiction ?? $"All {cand.Invalid} row(s) invalid");
            if (existing is not null) details.Add("approved data unchanged");
            rec.Details = string.Join(" · ", details);
            return rec;
        }

        if (existing is null)
        {
            rec.CreateVote = cand.Details;
            foreach (var (code, b) in cand.Ballots)
            {
                rec.NewBallots.Add(new NewBallot(code, b.Ballot, b.RowNo));
                rec.Rows.Add(new RowResult(b.RowNo, cand.VoteId, code, b.Ballot.ToString(), RowOutcome.New, "New vote", null));
            }
            rec.NewCount = cand.Ballots.Count;
            rec.Outcome = "New";
            details.Add($"New vote · {rec.NewCount} country votes");
        }
        else
        {
            // ING-03 / CON-03: vote details compared field by field.
            CompareDetail(rec, existing, VoteFields.Session, existing.Details.Session.ToString(), cand.Details.Session.ToString(), details);
            CompareDetail(rec, existing, VoteFields.Date, existing.Details.Date.ToString("yyyy-MM-dd"), cand.Details.Date.ToString("yyyy-MM-dd"), details);
            CompareDetail(rec, existing, VoteFields.Title, existing.Details.Title, cand.Details.Title, details);

            var added = new List<string>();
            foreach (var (code, b) in cand.Ballots)
            {
                var incoming = b.Ballot.ToString();
                if (!existing.Ballots.TryGetValue(code, out var approved))
                {
                    // ING-10: completion of a vote that arrived in parts.
                    rec.NewBallots.Add(new NewBallot(code, b.Ballot, b.RowNo));
                    rec.Rows.Add(new RowResult(b.RowNo, cand.VoteId, code, incoming, RowOutcome.New, "Country added to existing vote", null));
                    rec.NewCount++;
                    added.Add($"{code}: {Ballots.Describe(b.Ballot)}");
                }
                else if (approved == b.Ballot)
                {
                    rec.Rows.Add(new RowResult(b.RowNo, cand.VoteId, code, incoming, RowOutcome.Unchanged, null, null));
                    rec.UnchangedCount++;
                }
                else
                {
                    // ING-08: never overwrite. Open a conflict unless a human already handled this exact pair (ING-12).
                    var approvedText = approved.ToString();
                    var reason = $"Approved value is {Ballots.Describe(approved)}, file says {Ballots.Describe(b.Ballot)}";
                    var known = FindKnown(existing, code, VoteFields.Ballot, approvedText, incoming);
                    if (known is null)
                    {
                        rec.NewConflicts.Add(new ConflictToOpen(ConflictKind.CountryVote, code, VoteFields.Ballot, approvedText, incoming));
                        details.Add($"{code} country vote: {Ballots.Describe(approved)} → {Ballots.Describe(b.Ballot)} (conflict)");
                    }
                    else
                    {
                        reason += $" — {DescribeKnown(known)}, not reopened";
                    }
                    rec.Rows.Add(new RowResult(b.RowNo, cand.VoteId, code, incoming, RowOutcome.Conflict, reason, null));
                    rec.ConflictRows++;
                }
            }

            if (added.Count is > 0 and <= 3) details.Add(string.Join(", ", added) + " added");
            else if (added.Count > 3) details.Add($"{added.Count} country votes added");
            rec.Outcome = rec.NewConflicts.Count > 0 ? "Conflict" : rec.NewCount > 0 ? "Updated" : "No change";
            if (rec.UnchangedCount > 0) details.Add($"{rec.UnchangedCount} unchanged");
        }

        if (cand.Duplicates > 0) details.Add($"{cand.Duplicates} duplicate row(s) ignored");
        if (cand.Invalid > 0) details.Add($"{cand.Invalid} invalid row(s)");
        if (cand.ContradictedCountries.Count > 0)
            details.Add($"{string.Join(", ", cand.ContradictedCountries)} not stored (contradiction)");
        rec.Details = string.Join(" · ", details);
        return rec;
    }

    private static void CompareDetail(VoteReconciliation rec, ExistingVote existing, string field, string approved, string incoming, List<string> details)
    {
        if (approved == incoming) return;
        var known = FindKnown(existing, "", field, approved, incoming);
        if (known is null)
        {
            rec.NewConflicts.Add(new ConflictToOpen(ConflictKind.VoteDetail, "", field, approved, incoming));
            details.Add($"Vote {FieldLabel(field)}: \"{approved}\" → \"{incoming}\" (conflict)");
        }
        else
        {
            details.Add($"Vote {FieldLabel(field)} differs — {DescribeKnown(known)}, not reopened");
        }
    }

    /// <summary>
    /// A conflict is not reopened when one is already pending with the same incoming value,
    /// or when a human already decided between exactly these two values.
    /// </summary>
    private static KnownConflict? FindKnown(ExistingVote existing, string code, string field, string approved, string incoming) =>
        existing.Conflicts.FirstOrDefault(c => c.CountryCode == code && c.Field == field && (
            (c.Status == ConflictStatus.Pending && c.IncomingValue == incoming) ||
            (c.Status != ConflictStatus.Pending &&
             ((c.ApprovedValue == approved && c.IncomingValue == incoming) ||
              (c.ApprovedValue == incoming && c.IncomingValue == approved)))));

    private static string DescribeKnown(KnownConflict c) => c.Status switch
    {
        ConflictStatus.Pending => $"conflict #{c.Id} is already waiting for a decision",
        ConflictStatus.Accepted => $"already decided in conflict #{c.Id} (accepted)",
        _ => $"already decided in conflict #{c.Id} (rejected)",
    };

    public static string FieldLabel(string field) => field switch
    {
        VoteFields.Title => "title",
        VoteFields.Date => "date",
        VoteFields.Session => "session",
        _ => field,
    };
}
