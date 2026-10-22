using Meridian.Core.Domain;
using Meridian.Core.Ingestion;

namespace Meridian.Core.Extraction;

public sealed class ExtractedRow
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Y / N / A / X, or null when the marks could not be read (e.g. two marks).</summary>
    public string? Ballot { get; set; }
    public double Confidence { get; set; } = 1.0;
    public int Page { get; set; }
    public List<string> Issues { get; set; } = new();
}

public sealed class PrintedTotals
{
    public int? Yes { get; set; }
    public int? No { get; set; }
    public int? Abstain { get; set; }
    public int? NotVoting { get; set; }
}

/// <summary>Output of the AI extraction. Treated as untrusted input (PDF-03).</summary>
public sealed class ExtractedSheet
{
    public string? VoteId { get; set; }
    public string? Session { get; set; }
    public string? Date { get; set; }
    public string? Title { get; set; }
    public List<ExtractedRow> Rows { get; set; } = new();
    public PrintedTotals? Totals { get; set; }
    public List<string> Issues { get; set; } = new();
    /// <summary>Problems found while reading the layout (kept across re-validation).</summary>
    public List<string> ParseIssues { get; set; } = new();
    public int Pages { get; set; }
}

/// <summary>
/// Domain checks on extracted data before an analyst sees it (PDF-03, PDF-04).
/// Does not change values; it only flags.
/// </summary>
public static class ExtractionValidator
{
    public const double LowConfidence = 0.80;

    public static void Validate(ExtractedSheet sheet, MemberRegistry members)
    {
        sheet.Issues = new List<string>(sheet.ParseIssues);
        foreach (var r in sheet.Rows) r.Issues.Clear();

        if (string.IsNullOrWhiteSpace(sheet.VoteId)) sheet.Issues.Add("Vote id not found");
        if (!int.TryParse(sheet.Session, out _)) sheet.Issues.Add("Session not found or not a number");
        DateOnly? date = null;
        if (sheet.Date is null || !RowValidator.TryParseDate(sheet.Date, out var d, out _))
            sheet.Issues.Add("Vote date not found or not in yyyy-mm-dd");
        else date = d;
        if (string.IsNullOrWhiteSpace(sheet.Title)) sheet.Issues.Add("Resolution title not found");

        var seen = new Dictionary<string, ExtractedRow>(StringComparer.Ordinal);
        foreach (var r in sheet.Rows)
        {
            r.Code = r.Code.Trim().ToUpperInvariant();
            if (!members.Exists(r.Code) && CleanOcrCode(r.Code) is { } cleaned && members.Exists(cleaned))
            {
                // OCR noise around a code (e.g. "MW.I", "THẢ:"): suggest the clean code, but never silently — the row needs a human check.
                r.Issues.Add($"Code read as '{r.Code}', suggested '{cleaned}'");
                r.Code = cleaned;
                r.Confidence = Math.Min(r.Confidence, LowConfidence - 0.01);
            }
            if (!members.Exists(r.Code)) r.Issues.Add("Unknown country code");
            else if (date is not null && !members.IsMemberOn(r.Code, date.Value)) r.Issues.Add("Not a member on the vote date");

            if (seen.TryGetValue(r.Code, out var first))
            {
                r.Issues.Add($"Appears twice (p.{first.Page}, p.{r.Page})");
                if (!first.Issues.Any(i => i.StartsWith("Appears twice"))) first.Issues.Add($"Appears twice (p.{first.Page}, p.{r.Page})");
            }
            else seen[r.Code] = r;

            if (r.Ballot is null || r.Ballot.Length != 1 || !Ballots.IsValid(r.Ballot[0])) r.Issues.Add("Ballot unreadable");
            if (r.Confidence < LowConfidence) r.Issues.Add("Low confidence");
        }

        if (date is not null)
        {
            var missing = members.Codes.Where(c => members.IsMemberOn(c, date.Value) && !seen.ContainsKey(c)).OrderBy(c => c).ToList();
            if (missing.Count > 0) sheet.Issues.Add($"Member state(s) missing from the sheet: {string.Join(", ", missing)}");
        }

        if (sheet.Totals is null) sheet.Issues.Add("Printed totals not found on the sheet");
        else
        {
            var counted = Count(sheet);
            var t = sheet.Totals;
            if (t.Yes != counted.Yes || t.No != counted.No || t.Abstain != counted.Abstain || t.NotVoting != counted.NotVoting)
                sheet.Issues.Add($"Totals do not match the sheet. Printed: YES {t.Yes} · NO {t.No} · ABSTAIN {t.Abstain} · ABSENT {t.NotVoting}. " +
                                 $"Extracted: YES {counted.Yes} · NO {counted.No} · ABSTAIN {counted.Abstain} · ABSENT {counted.NotVoting}");
        }
    }

    /// <summary>Removes accents and non-letters: "MW.I" → "MWI", "THẢ:" → "THA". Null if not 3 letters after cleaning.</summary>
    public static string? CleanOcrCode(string code)
    {
        var letters = code.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => c is >= 'A' and <= 'Z')
            .ToArray();
        return letters.Length == 3 ? new string(letters) : null;
    }

    public static PrintedTotals Count(ExtractedSheet sheet) => new()
    {
        Yes = sheet.Rows.Count(r => r.Ballot == "Y"),
        No = sheet.Rows.Count(r => r.Ballot == "N"),
        Abstain = sheet.Rows.Count(r => r.Ballot == "A"),
        NotVoting = sheet.Rows.Count(r => r.Ballot == "X"),
    };

    public static bool HasIssues(ExtractedSheet sheet) => sheet.Issues.Count > 0 || sheet.Rows.Any(r => r.Issues.Count > 0);

    /// <summary>Approved extraction becomes ordinary import rows, so it follows the CSV rules (PDF-05).</summary>
    public static IEnumerable<RawRow> ToRawRows(ExtractedSheet sheet, string sourceName, DateTimeOffset publishedAt)
    {
        var i = 1;
        foreach (var r in sheet.Rows)
        {
            i++;
            var fields = new Dictionary<string, string>
            {
                ["assembly_vote_id"] = sheet.VoteId ?? "",
                ["session"] = sheet.Session ?? "",
                ["vote_date"] = sheet.Date ?? "",
                ["resolution_title"] = sheet.Title ?? "",
                ["country_code"] = r.Code,
                ["country_name"] = r.Name,
                ["ballot"] = r.Ballot ?? "",
                ["published_at"] = publishedAt.ToString("O"),
            };
            yield return new RawRow(i, $"{sourceName} p.{r.Page}: {r.Code},{r.Name},{r.Ballot}", fields, null);
        }
    }
}
