using FluentAssertions;
using Meridian.Core.Domain;
using Meridian.Core.Ingestion;

namespace Meridian.UnitTests;

/// <summary>Section 7.1 rules, tested on the pure ingestion engine (no database).</summary>
public class IngestionRulesTests
{
    private static readonly MemberRegistry Members = TestData.Members();

    private static Dictionary<int, RowOutcome> Classify(FileAnalysis a, Func<string, ExistingVote?>? existing = null)
    {
        var map = a.Rejected.ToDictionary(r => r.RowNo, r => r.Outcome);
        foreach (var v in a.Votes)
            foreach (var r in VoteReconciler.Reconcile(v, existing?.Invoke(v.VoteId)).Rows)
                map[r.RowNo] = r.Outcome;
        return map;
    }

    [Fact(DisplayName = "7.2 original file: every row gets exactly one classification (ING-06)")]
    public void OriginalFile_ClassifiedAsInSpec()
    {
        var a = FileAnalyzer.Analyze(TestData.Read(TestData.OriginalFile()), Members);
        var outcomes = Classify(a);

        outcomes.Should().HaveCount(8, "each of the 8 rows gets one class");
        outcomes[2].Should().Be(RowOutcome.New);
        outcomes[3].Should().Be(RowOutcome.New);
        outcomes[4].Should().Be(RowOutcome.New);
        outcomes[5].Should().Be(RowOutcome.Duplicate);
        outcomes[6].Should().Be(RowOutcome.Contradiction);
        outcomes[7].Should().Be(RowOutcome.Contradiction);
        outcomes[8].Should().Be(RowOutcome.Invalid);
        outcomes[9].Should().Be(RowOutcome.Invalid);
    }

    [Fact(DisplayName = "ING-03: rows are grouped by vote id even when not contiguous")]
    public void Grouping_NonContiguousRows()
    {
        var a = FileAnalyzer.Analyze(TestData.Read(
            TestData.Row("MA/2024/001", "BRA", "Y"),
            TestData.Row("MA/2024/002", "BRA", "N", "Other"),
            TestData.Row("MA/2024/001", "IND", "N")), Members);

        a.Votes.Should().HaveCount(2);
        a.Votes.Single(v => v.VoteId == "MA/2024/001").Ballots.Keys.Should().BeEquivalentTo("BRA", "IND");
    }

    [Theory(DisplayName = "ING-04: each row is validated, with row number, reason and original value")]
    [InlineData("MA/2024/001,79,2024-01-11,\"T\",XXX,Nowhere,Y,2024-04-02T09:00:00Z", "Unknown country code 'XXX'")]
    [InlineData("MA/2024/001,79,2024-01-11,\"T\",BRA,Brazil,Yes,2024-04-02T09:00:00Z", "Invalid ballot 'Yes'")]
    [InlineData("MA/2024/001,79,2024-01-11,\"T\",BRA,Brazil,,2024-04-02T09:00:00Z", "Missing required field(s): ballot")]
    [InlineData("MA/2024/001,79,21/02/2024,\"T\",BRA,Brazil,Y,2024-04-02T09:00:00Z", "Invalid date '21/02/2024'")]
    [InlineData("MA/2024/001,79,2024-01-11,\"T\",AGO;N,2024-04-02T09:00:00Z", "Expected 8 columns, found 6")]
    [InlineData("MA/2024/001,79,2024-01-11,T, with comma,BRA,Brazil,Y,2024-04-02T09:00:00Z", "Expected 8 columns, found 9")]
    [InlineData("MA/2024/001,x,2024-01-11,\"T\",BRA,Brazil,Y,2024-04-02T09:00:00Z", "Invalid session 'x'")]
    [InlineData(",79,2024-01-11,\"T\",BRA,Brazil,Y,2024-04-02T09:00:00Z", "Missing required field(s): assembly_vote_id")]
    [InlineData("MA/2024/001,79,2024-01-11,\"T\",NZL,New Zealand,Y,2024-04-02T09:00:00Z", "New Zealand (NZL) was not a member on 2024-01-11")]
    public void InvalidRows_AreRejectedWithReason(string row, string reason)
    {
        var a = FileAnalyzer.Analyze(TestData.Read(row), Members);

        var r = a.Rejected.Should().ContainSingle().Subject;
        r.Outcome.Should().Be(RowOutcome.Invalid);
        r.RowNo.Should().Be(2);
        r.Reason.Should().StartWith(reason);
        r.Raw.Should().Be(row, "the original value is kept for the report");
    }

    [Fact(DisplayName = "ING-04: membership uses all periods (left and re-joined)")]
    public void Membership_LeftAndReturned()
    {
        Members.IsMemberOn("ISL", new DateOnly(2018, 5, 1)).Should().BeTrue();
        Members.IsMemberOn("ISL", new DateOnly(2020, 5, 1)).Should().BeFalse();
        Members.IsMemberOn("ISL", new DateOnly(2024, 5, 1)).Should().BeTrue();
    }

    [Fact(DisplayName = "ING-05: vote details must be identical in every row of the vote, otherwise the whole vote is a contradiction")]
    public void DetailsDiffer_AllRowsContradiction()
    {
        var a = FileAnalyzer.Analyze(TestData.Read(
            TestData.Row("MA/2024/025", "BRA", "Y", "Nuclear Non-Proliferation"),
            TestData.Row("MA/2024/025", "IND", "Y", "Nuclear Non-Proliferation (draft)")), Members);

        a.Rejected.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == RowOutcome.Contradiction);
        a.Votes.Single().Details.Should().BeNull();
        VoteReconciler.Reconcile(a.Votes.Single(), null).CreateVote.Should().BeNull("the system does not pick a value");
    }

    [Fact(DisplayName = "ING-05: a contradiction for one country does not block the rest of the vote")]
    public void CountryContradiction_OthersStillStored()
    {
        var a = FileAnalyzer.Analyze(TestData.Read(TestData.OriginalFile()), Members);
        var rec = VoteReconciler.Reconcile(a.Votes.Single(v => v.VoteId == "MA/2024/103"), null);
        rec.CreateVote.Should().NotBeNull();
        rec.NewBallots.Should().BeEmpty("FRA had two different values; neither is stored");
    }

    [Fact(DisplayName = "ING-07: an invalid row does not stop the rest of the file")]
    public void InvalidRow_DoesNotStopFile()
    {
        var a = FileAnalyzer.Analyze(TestData.Read(
            "garbage,\"unterminated",
            TestData.Row("MA/2024/001", "BRA", "Y")), Members);

        a.Votes.Single().Ballots.Should().ContainKey("BRA");
    }

    [Fact(DisplayName = "ING-13: normalisation is reported, not silent (lower-case code, extra spaces)")]
    public void Normalization_IsReported()
    {
        var a = FileAnalyzer.Analyze(TestData.Read(
            TestData.Row("MA/2024/018", "deu", "Y", "Polar  Research")), Members);

        a.Votes.Single().Ballots.Should().ContainKey("DEU");
        a.Votes.Single().Details!.Title.Should().Be("Polar Research");
        a.Notes.Should().Contain(n => n.Field == "country_code" && n.Original == "deu" && n.Normalized == "DEU");
        a.Notes.Should().Contain(n => n.Field == "resolution_title" && n.Rule == "collapse spaces");
    }

    [Fact(DisplayName = "ING-04: dd-mm-yyyy (as in ING-04) is accepted and normalised to ISO; the change is reported")]
    public void DayFirstDate_AcceptedAndReported()
    {
        var a = FileAnalyzer.Analyze(TestData.Read(TestData.Row("MA/2024/001", "BRA", "Y", date: "11-01-2024")), Members);
        a.Votes.Single().Details!.Date.Should().Be(new DateOnly(2024, 1, 11));
        a.Notes.Should().ContainSingle(n => n.Field == "vote_date");
    }

    [Fact(DisplayName = "BOM and header are handled; a missing column fails the import with a clear message")]
    public void Header_BomAndMissingColumns()
    {
        var bom = "﻿" + TestData.Header + "\n" + TestData.Row("MA/2024/001", "BRA", "Y") + "\n";
        CsvRowReader.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bom))).Should().ContainSingle(r => r.Fields != null);

        var act = () => CsvRowReader.Read(new MemoryStream("a,b\n1,2\n"u8.ToArray())).ToList();
        act.Should().Throw<CsvHeaderException>().WithMessage("*Missing column(s)*");
    }
}
