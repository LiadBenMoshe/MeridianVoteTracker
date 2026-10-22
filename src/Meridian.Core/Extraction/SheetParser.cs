using System.Globalization;
using System.Text.RegularExpressions;

namespace Meridian.Core.Extraction;

/// <summary>Neutral view of an OCR/layout result, so parsing does not depend on the Azure SDK.</summary>
public sealed record LayoutCell(string Text, double Confidence);

public sealed record LayoutTable(int Page, IReadOnlyList<IReadOnlyList<LayoutCell>> Rows);

public sealed record LayoutDocument(IReadOnlyList<string> PageTexts, IReadOnlyList<LayoutTable> Tables);

/// <summary>Turns a layout result of an Assembly "Record of vote" sheet into an <see cref="ExtractedSheet"/>.</summary>
public static partial class SheetParser
{
    [GeneratedRegex(@"\b([A-Z]{2}/\d{4}/\d{1,5})\b")]
    private static partial Regex VoteIdRx();

    [GeneratedRegex(@"Session\s*:?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SessionRx();

    [GeneratedRegex(@"Date of vote\s*:?\s*(\d{1,2}\s+[A-Za-z]+\s+\d{4}|\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex DateRx();

    [GeneratedRegex(@"Resolution\s*:?\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRx();

    [GeneratedRegex(@"Yes\s*:?\s*(\d+).{0,10}?No\s*:?\s*(\d+).{0,10}?Abstentions?\s*:?\s*(\d+).{0,10}?Not\s*voting\s*:?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TotalsRx();

    private static readonly HashSet<string> MarkTexts = new(StringComparer.OrdinalIgnoreCase) { "x", "×", "✗", "✘", "☒", ":selected:", "k", "+" };

    public static ExtractedSheet Parse(LayoutDocument doc)
    {
        var sheet = new ExtractedSheet { Pages = doc.PageTexts.Count };
        var first = doc.PageTexts.FirstOrDefault() ?? "";

        sheet.VoteId = VoteIdRx().Match(first) is { Success: true } v ? v.Groups[1].Value : null;
        sheet.Session = SessionRx().Match(first) is { Success: true } s ? s.Groups[1].Value : null;
        if (DateRx().Match(first) is { Success: true } d)
        {
            var text = d.Groups[1].Value.Trim();
            sheet.Date = DateTime.TryParseExact(text, ["d MMMM yyyy", "dd MMMM yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt.ToString("yyyy-MM-dd")
                : text;
        }
        if (TitleRx().Match(first) is { Success: true } t)
            sheet.Title = t.Groups[1].Value.Split('\n')[0].Trim();

        // Every page repeats the header; they must agree.
        for (var i = 1; i < doc.PageTexts.Count; i++)
        {
            var other = VoteIdRx().Match(doc.PageTexts[i]);
            if (other.Success && sheet.VoteId is not null && other.Groups[1].Value != sheet.VoteId)
                sheet.ParseIssues.Add($"Page {i + 1} shows vote {other.Groups[1].Value}, page 1 shows {sheet.VoteId}");
        }

        var all = string.Join("\n", doc.PageTexts);
        if (TotalsRx().Match(all) is { Success: true } m)
        {
            sheet.Totals = new PrintedTotals
            {
                Yes = int.Parse(m.Groups[1].Value),
                No = int.Parse(m.Groups[2].Value),
                Abstain = int.Parse(m.Groups[3].Value),
                NotVoting = int.Parse(m.Groups[4].Value),
            };
        }

        foreach (var table in doc.Tables) ParseTable(table, sheet);
        return sheet;
    }

    private static void ParseTable(LayoutTable table, ExtractedSheet sheet)
    {
        int code = -1, name = -1, yes = -1, no = -1, abst = -1, headerRow = -1;
        for (var r = 0; r < table.Rows.Count && headerRow < 0; r++)
        {
            var cells = table.Rows[r];
            for (var c = 0; c < cells.Count; c++)
            {
                var text = Normalize(cells[c].Text);
                if (text == "CODE") code = c;
                else if (text.StartsWith("MEMBER")) name = c;
                else if (text == "YES") yes = c;
                else if (text == "NO") no = c;
                else if (text.StartsWith("ABST")) abst = c;
            }
            if (code >= 0 && yes >= 0 && no >= 0 && abst >= 0) headerRow = r;
            else code = name = yes = no = abst = -1;
        }
        if (headerRow < 0) return;

        for (var r = headerRow + 1; r < table.Rows.Count; r++)
        {
            var cells = table.Rows[r];
            LayoutCell Cell(int i) => i >= 0 && i < cells.Count ? cells[i] : new LayoutCell("", 1);
            // Layout OCR reports empty check boxes as ":unselected:"; an empty table line is not a country.
            var codeText = StripMarks(Cell(code).Text);
            if (codeText.Length == 0 && cells.All(c => StripMarks(c.Text).Length == 0)) continue;

            var row = new ExtractedRow { Code = codeText.ToUpperInvariant().Replace("0", "O"), Name = Cell(name).Text.Trim(), Page = table.Page };
            var marks = new List<(string Ballot, LayoutCell Cell)>();
            foreach (var (col, ballot) in new[] { (yes, "Y"), (no, "N"), (abst, "A") })
            {
                var cell = Cell(col);
                var text = cell.Text.Replace(":unselected:", "").Trim();
                if (text.Length > 0) marks.Add((ballot, cell));
            }

            var confidences = new List<double> { Cell(code).Confidence };
            if (marks.Count == 0) row.Ballot = "X"; // empty row = absent
            else if (marks.Count == 1)
            {
                row.Ballot = marks[0].Ballot;
                confidences.Add(marks[0].Cell.Confidence);
                // Something was written, but not an obvious mark: keep it, but lower confidence.
                if (!MarkTexts.Contains(marks[0].Cell.Text.Trim())) confidences.Add(0.5);
            }
            else
            {
                row.Ballot = null; // flagged as "Ballot unreadable" by the validator
                confidences.AddRange(marks.Select(x => x.Cell.Confidence));
            }
            row.Confidence = Math.Round(confidences.Min(), 2);
            sheet.Rows.Add(row);
        }
    }

    private static string StripMarks(string s) => s.Replace(":unselected:", "").Trim();

    private static string Normalize(string s) => s.Trim().TrimEnd('.').ToUpperInvariant();
}
