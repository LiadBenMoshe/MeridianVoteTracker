using FluentAssertions;
using Meridian.Core.Domain;
using Meridian.Core.Ingestion;

namespace Meridian.UnitTests;

/// <summary>The corrected file of section 7.2 against approved data (ING-06, ING-08..12, CON-03).</summary>
public class ReconciliationTests
{
    private static readonly MemberRegistry Members = TestData.Members();

    /// <summary>Approved state after the original 7.2 upload.</summary>
    private static Dictionary<string, ExistingVote> ApprovedAfterOriginal() => new()
    {
        ["MA/2024/101"] = new ExistingVote
        {
            Details = new VoteDetails(79, new DateOnly(2024, 1, 11), "Maritime Safety"),
            Ballots = new() { ["BRA"] = 'Y', ["IND"] = 'Y' },
        },
        ["MA/2024/102"] = new ExistingVote { Details = new VoteDetails(79, new DateOnly(2024, 1, 11), "Food Security"), Ballots = new() { ["JPN"] = 'Y' } },
        ["MA/2024/103"] = new ExistingVote { Details = new VoteDetails(79, new DateOnly(2024, 1, 18), "Cultural Heritage") },
        ["MA/2024/104"] = new ExistingVote { Details = new VoteDetails(79, new DateOnly(2024, 2, 6), "Ocean Plastic") },
    };

    private static Dictionary<string, VoteReconciliation> Run(Dictionary<string, ExistingVote> approved, string[] file)
    {
        var a = FileAnalyzer.Analyze(TestData.Read(file), Members);
        return a.Votes.ToDictionary(v => v.VoteId, v => VoteReconciler.Reconcile(v, approved.GetValueOrDefault(v.VoteId)));
    }

    [Fact(DisplayName = "7.2 corrected file: conflict, unchanged, new, completion, new vote, title conflict")]
    public void RevisedFile_MatchesSpec()
    {
        var r = Run(ApprovedAfterOriginal(), TestData.RevisedFile());

        var v101 = r["MA/2024/101"];
        v101.Rows.Single(x => x.CountryCode == "BRA").Outcome.Should().Be(RowOutcome.Conflict);
        v101.Rows.Single(x => x.CountryCode == "IND").Outcome.Should().Be(RowOutcome.Unchanged);
        v101.NewConflicts.Should().HaveCount(2);
        v101.NewConflicts.Should().ContainSingle(c => c.Kind == ConflictKind.CountryVote && c.ApprovedValue == "Y" && c.IncomingValue == "A");
        v101.NewConflicts.Should().ContainSingle(c => c.Kind == ConflictKind.VoteDetail && c.Field == VoteFields.Title);
        v101.NewBallots.Should().BeEmpty("approved data is never overwritten (ING-08)");

        r["MA/2024/103"].Rows.Single().Outcome.Should().Be(RowOutcome.New, "the earlier value was never stored");
        r["MA/2024/104"].Rows.Single().Outcome.Should().Be(RowOutcome.New, "missing country completes the vote (ING-10)");
        r["MA/2024/104"].NewConflicts.Should().BeEmpty();
        r["MA/2024/105"].CreateVote.Should().NotBeNull();
        r.Should().NotContainKey("MA/2024/102", "a vote absent from the file is not touched (ING-09)");
    }

    [Fact(DisplayName = "ING-11: a different country name for the same code is not a conflict")]
    public void DifferentName_SameCode_NoConflict()
    {
        var approved = new Dictionary<string, ExistingVote>
        {
            ["MA/2024/001"] = new() { Details = new VoteDetails(79, new DateOnly(2024, 1, 11), "Maritime Safety"), Ballots = new() { ["TUR"] = 'Y' } },
        };
        var r = Run(approved, [TestData.Row("MA/2024/001", "TUR", "Y", name: "Turkey")]);
        r["MA/2024/001"].Rows.Single().Outcome.Should().Be(RowOutcome.Unchanged);
    }

    [Fact(DisplayName = "ING-12: a conflict already waiting for a decision is not opened again")]
    public void PendingConflict_NotReopened()
    {
        var approved = ApprovedAfterOriginal();
        approved["MA/2024/101"].Conflicts.Add(new KnownConflict(7, "BRA", VoteFields.Ballot, "Y", "A", ConflictStatus.Pending));
        approved["MA/2024/101"].Conflicts.Add(new KnownConflict(8, "", VoteFields.Title, "Maritime Safety", "Maritime Safety: Revised", ConflictStatus.Pending));

        var r = Run(approved, TestData.RevisedFile());
        r["MA/2024/101"].NewConflicts.Should().BeEmpty();
        r["MA/2024/101"].Rows.Single(x => x.CountryCode == "BRA").Reason.Should().Contain("already waiting");
    }

    [Fact(DisplayName = "ING-12: a pair of values a human already decided on is not reopened (re-upload after a decision)")]
    public void DecidedConflict_NotReopened()
    {
        var approved = ApprovedAfterOriginal();
        approved["MA/2024/101"].Ballots["BRA"] = 'A'; // Dana accepted Y → A
        approved["MA/2024/101"].Conflicts.Add(new KnownConflict(7, "BRA", VoteFields.Ballot, "Y", "A", ConflictStatus.Accepted));

        // Re-uploading the ORIGINAL file (BRA = Y) must not reopen the same question.
        var r = Run(approved, [TestData.Row("MA/2024/101", "BRA", "Y")]);
        r["MA/2024/101"].NewConflicts.Should().BeEmpty();
    }

    [Fact(DisplayName = "CON-03: a changed date is a vote-detail conflict too")]
    public void DateChange_IsDetailConflict()
    {
        var approved = new Dictionary<string, ExistingVote>
        {
            ["MA/2024/014"] = new() { Details = new VoteDetails(79, new DateOnly(2024, 2, 5), "Polar Research") },
        };
        var r = Run(approved, [TestData.Row("MA/2024/014", "BRA", "Y", "Polar Research", "2024-02-06")]);
        r["MA/2024/014"].NewConflicts.Should().ContainSingle(c => c.Field == VoteFields.Date && c.ApprovedValue == "2024-02-05" && c.IncomingValue == "2024-02-06");
        r["MA/2024/014"].Rows.Single().Outcome.Should().Be(RowOutcome.New, "the country vote itself is new");
    }

    [Fact(DisplayName = "ING-12: re-uploading the same file gives 0 new rows and 0 new conflicts")]
    public void SameFileTwice_NothingNew()
    {
        var a = FileAnalyzer.Analyze(TestData.Read(TestData.OriginalFile()), Members);
        var approved = new Dictionary<string, ExistingVote>();
        foreach (var v in a.Votes)
        {
            var rec = VoteReconciler.Reconcile(v, null);
            if (rec.CreateVote is null) continue;
            approved[v.VoteId] = new ExistingVote { Details = rec.CreateVote, Ballots = rec.NewBallots.ToDictionary(b => b.CountryCode, b => b.Ballot) };
        }

        var again = Run(approved, TestData.OriginalFile());
        again.Values.Sum(v => v.NewCount).Should().Be(0);
        again.Values.Sum(v => v.NewConflicts.Count).Should().Be(0);
    }
}
