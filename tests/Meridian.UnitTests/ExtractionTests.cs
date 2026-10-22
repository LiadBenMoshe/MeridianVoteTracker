using FluentAssertions;
using Meridian.Core.Extraction;
using Meridian.Core.Ingestion;

namespace Meridian.UnitTests;

/// <summary>Bonus section 8: parsing a layout result and treating AI output as untrusted (PDF-03/04/05).</summary>
public class ExtractionTests
{
    private static LayoutCell C(string t, double conf = 0.99) => new(t, conf);

    private static LayoutDocument Sheet(IReadOnlyList<IReadOnlyList<LayoutCell>> rows, string totals = "Recorded totals: Yes: 2 No: 1 Abstentions: 0 Not voting: 1") =>
        new(
            [$"MERIDIAN ASSEMBLY\nRECORD OF VOTE\nVote No.:\nMA/2011/042\nSession:\n66\nDate of vote:\n4 October 2011\nResolution:\nProtection of Cultural Heritage\n{totals}"],
            [new LayoutTable(1, [[C("Code"), C("Member state"), C("YES"), C("NO"), C("ABST.")], .. rows])]);

    [Fact(DisplayName = "Parser reads header, marks, empty row = absent, and printed totals")]
    public void Parse_ReadsSheet()
    {
        var s = SheetParser.Parse(Sheet(
        [
            [C("BRA"), C("Brazil"), C("X"), C(""), C("")],
            [C("IND"), C("India"), C(":selected:"), C(""), C("")],
            [C("JPN"), C("Japan"), C(""), C("×"), C("")],
            [C("FRA"), C("France"), C(""), C(""), C("")],
        ]));

        s.VoteId.Should().Be("MA/2011/042");
        s.Session.Should().Be("66");
        s.Date.Should().Be("2011-10-04");
        s.Title.Should().Be("Protection of Cultural Heritage");
        s.Rows.Select(r => (r.Code, r.Ballot)).Should().Equal(("BRA", "Y"), ("IND", "Y"), ("JPN", "N"), ("FRA", "X"));
        s.Totals!.Yes.Should().Be(2);
        s.Totals.NotVoting.Should().Be(1);
    }

    [Fact(DisplayName = "Empty table lines (OCR ':unselected:' marks) are not countries — found on the real MA_2011_042 sheet")]
    public void EmptyLines_WithUnselectedMarks_AreSkipped()
    {
        var s = SheetParser.Parse(Sheet(
        [
            [C("BRA"), C("Brazil"), C("X"), C(""), C("")],
            [C(":unselected:"), C(""), C(":unselected:"), C(":unselected:"), C(":unselected:")],
        ]));
        s.Rows.Should().ContainSingle(r => r.Code == "BRA");
    }

    [Fact(DisplayName = "PDF-03: unknown code, duplicate country, low confidence, two marks and totals mismatch are flagged")]
    public void Validator_FlagsProblems()
    {
        var s = SheetParser.Parse(Sheet(
        [
            [C("BRA"), C("Brazil"), C("X"), C(""), C("")],
            [C("ANT"), C("Netherlands Antilles"), C("X"), C(""), C("")],
            [C("BRA"), C("Brazil"), C("X"), C(""), C("")],
            [C("JPN"), C("Japan"), C("X", 0.41), C(""), C("")],
            [C("IND"), C("India"), C("X"), C("X"), C("")],
        ]));
        ExtractionValidator.Validate(s, TestData.Members());

        s.Rows.Single(r => r.Code == "ANT").Issues.Should().Contain("Unknown country code");
        s.Rows.Where(r => r.Code == "BRA").Should().OnlyContain(r => r.Issues.Any(i => i.StartsWith("Appears twice")));
        s.Rows.Single(r => r.Code == "JPN").Issues.Should().Contain("Low confidence");
        s.Rows.Single(r => r.Code == "IND").Issues.Should().Contain("Ballot unreadable");
        s.Issues.Should().Contain(i => i.StartsWith("Totals do not match"));
        ExtractionValidator.HasIssues(s).Should().BeTrue();
    }

    [Fact(DisplayName = "PDF-03: OCR noise around a code is cleaned only to a real member code, and the row stays flagged")]
    public void OcrNoise_InCode_SuggestedButFlagged()
    {
        var s = SheetParser.Parse(Sheet([[C("BR.A"), C("Brazil"), C("X"), C(""), C("")], [C("Q.Q.Q"), C("?"), C("X"), C(""), C("")]]));
        ExtractionValidator.Validate(s, TestData.Members());

        var bra = s.Rows[0];
        bra.Code.Should().Be("BRA");
        bra.Issues.Should().Contain(i => i.Contains("suggested 'BRA'")).And.Contain("Low confidence");
        s.Rows[1].Issues.Should().Contain("Unknown country code");
    }

    [Fact(DisplayName = "PDF-05: an approved extraction becomes normal import rows (same rules as CSV)")]
    public void Approved_BecomesImportRows()
    {
        var s = SheetParser.Parse(Sheet([[C("BRA"), C("Brazil"), C("X"), C(""), C("")]]));
        var raw = ExtractionValidator.ToRawRows(s, "sheet.pdf", DateTimeOffset.UtcNow).ToList();

        var a = FileAnalyzer.Analyze(raw, TestData.Members());
        a.Rejected.Should().BeEmpty();
        a.Votes.Single().VoteId.Should().Be("MA/2011/042");
        a.Votes.Single().Ballots["BRA"].Ballot.Should().Be('Y');
    }
}
