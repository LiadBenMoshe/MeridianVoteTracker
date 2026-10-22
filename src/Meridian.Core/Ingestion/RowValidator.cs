using System.Globalization;
using System.Text.RegularExpressions;
using Meridian.Core.Domain;

namespace Meridian.Core.Ingestion;

public sealed record VoteDetails(int Session, DateOnly Date, string Title)
{
    public string Describe() => $"session {Session}, {Date:yyyy-MM-dd}, \"{Title}\"";
}

/// <summary>A row that passed all ING-04 checks. Values are already normalized.</summary>
public sealed record ValidRow(
    int RowNo,
    string Raw,
    string VoteId,
    VoteDetails Details,
    string CountryCode,
    string CountryName,
    char Ballot,
    DateTimeOffset PublishedAt,
    IReadOnlyList<string> Notes);

/// <summary>Normalisation applied to a value. Reported, never silent (ING-13).</summary>
public sealed record NormalizationNote(int RowNo, string Field, string Original, string Normalized, string Rule);

public sealed record ValidationResult(ValidRow? Row, string? Reason, string? VoteId, string? CountryCode, string? BallotText, IReadOnlyList<NormalizationNote> Notes);

/// <summary>Row-level checks (ING-04).</summary>
public sealed partial class RowValidator(MemberRegistry members)
{
    public static readonly string[] NormalizationRules =
    [
        "Leading/trailing spaces are trimmed from every field.",
        "Country codes are upper-cased (e.g. 'deu' -> 'DEU').",
        "Repeated spaces inside a resolution title are collapsed to one.",
        "Dates are accepted as yyyy-mm-dd (export format, Appendix A) or dd-mm-yyyy (ING-04). Anything else (e.g. 21/02/2024) is rejected.",
        "Ballot must be exactly Y, N, A or X. Values like 'Yes', '0' or empty are rejected, not guessed.",
        "Country name is informational only; the ISO code is the identity (ING-11).",
    ];

    [GeneratedRegex(@"^[A-Z]{2,4}/\d{4}/\d{1,5}$")]
    private static partial Regex VoteIdRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    public ValidationResult Validate(RawRow raw)
    {
        if (raw.Fields is null)
            return Fail(raw, raw.StructureError ?? "Malformed row");

        var notes = new List<NormalizationNote>();
        string F(string name) => raw.Fields[name];

        string Trimmed(string name)
        {
            var v = F(name);
            var t = v.Trim();
            if (t != v) notes.Add(new(raw.RowNo, name, v, t, "trim"));
            return t;
        }

        var voteId = Trimmed("assembly_vote_id");
        var sessionText = Trimmed("session");
        var dateText = Trimmed("vote_date");
        var title = Trimmed("resolution_title");
        var codeText = Trimmed("country_code");
        var name = Trimmed("country_name");
        var ballotText = Trimmed("ballot");
        var publishedText = Trimmed("published_at");

        var code = codeText.ToUpperInvariant();
        if (code != codeText && code.Length > 0)
            notes.Add(new(raw.RowNo, "country_code", codeText, code, "upper-case"));

        var collapsed = MultiSpace().Replace(title, " ");
        if (collapsed != title)
        {
            notes.Add(new(raw.RowNo, "resolution_title", title, collapsed, "collapse spaces"));
            title = collapsed;
        }

        ValidationResult Bad(string reason) =>
            new(null, reason, voteId.Length > 0 ? voteId : null, code.Length > 0 ? code : null, ballotText, notes);

        // Required fields
        var missing = new List<string>();
        if (voteId.Length == 0) missing.Add("assembly_vote_id");
        if (sessionText.Length == 0) missing.Add("session");
        if (dateText.Length == 0) missing.Add("vote_date");
        if (title.Length == 0) missing.Add("resolution_title");
        if (code.Length == 0) missing.Add("country_code");
        if (ballotText.Length == 0) missing.Add("ballot");
        if (publishedText.Length == 0) missing.Add("published_at");
        if (missing.Count > 0) return Bad($"Missing required field(s): {string.Join(", ", missing)}");

        if (!VoteIdRegex().IsMatch(voteId)) return Bad($"Invalid vote id '{voteId}'");
        if (!int.TryParse(sessionText, NumberStyles.None, CultureInfo.InvariantCulture, out var session) || session <= 0)
            return Bad($"Invalid session '{sessionText}'");

        if (!TryParseDate(dateText, out var date, out var dateNormalized))
            return Bad($"Invalid date '{dateText}' (expected yyyy-mm-dd or dd-mm-yyyy)");
        if (dateNormalized) notes.Add(new(raw.RowNo, "vote_date", dateText, date.ToString("yyyy-MM-dd"), "dd-mm-yyyy -> yyyy-mm-dd"));

        if (!members.Exists(code)) return Bad($"Unknown country code '{codeText}'");

        if (ballotText.Length != 1 || !Ballots.IsValid(ballotText[0]))
            return Bad($"Invalid ballot '{ballotText}' (expected Y, N, A or X)");

        if (!DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var published))
            return Bad($"Invalid published_at '{publishedText}'");

        if (!members.IsMemberOn(code, date))
            return Bad($"{members.NameOf(code)} ({code}) was not a member on {date:yyyy-MM-dd}");

        var row = new ValidRow(raw.RowNo, raw.Raw, voteId, new VoteDetails(session, date, title), code, name,
            ballotText[0], published, notes.Select(n => $"{n.Field}: '{n.Original}' -> '{n.Normalized}'").ToList());
        return new ValidationResult(row, null, voteId, code, ballotText, notes);
    }

    private static ValidationResult Fail(RawRow raw, string reason) => new(null, reason, null, null, null, []);

    public static bool TryParseDate(string text, out DateOnly date, out bool normalized)
    {
        normalized = false;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (DateOnly.TryParseExact(text, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            normalized = true;
            return true;
        }
        return false;
    }
}
