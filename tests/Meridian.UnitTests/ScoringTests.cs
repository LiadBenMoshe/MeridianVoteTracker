using FluentAssertions;
using Meridian.Core.Scoring;
using static Meridian.Core.Scoring.AlignmentScoring;

namespace Meridian.UnitTests;

/// <summary>Section 7.6 calculation rule and the decided open points.</summary>
public class ScoringTests
{
    [Fact(DisplayName = "7.6 example: YES on weight 4 (wanted YES) + NO on weight 2 (wanted YES) = 66.7")]
    public void SpecExample()
    {
        var s = Score([new VoteInput("v1", 'Y', 4), new VoteInput("v2", 'Y', 2)], new Dictionary<string, char> { ["v1"] = 'Y', ["v2"] = 'N' });
        s.Score.Should().BeApproximately(66.67, 0.01);
        Math.Round(s.Score!.Value, 1).Should().Be(66.7);
        s.Matched.Should().Be(4);
        s.Counted.Should().Be(6);
    }

    [Fact(DisplayName = "Abstain and absent are not counted")]
    public void AbstainAndAbsent_NotCounted()
    {
        var s = Score([new VoteInput("v1", 'Y', 5), new VoteInput("v2", 'Y', 3), new VoteInput("v3", 'N', 2)],
            new Dictionary<string, char> { ["v1"] = 'Y', ["v2"] = 'A', ["v3"] = 'X' });
        s.Score.Should().Be(100);
        s.Counted.Should().Be(5);
        s.Contributions.Single(c => c.VoteId == "v2").Note.Should().Contain("Abstain");
    }

    [Fact(DisplayName = "Votes while not a member (no ballot) are excluded")]
    public void NotMember_Excluded()
    {
        var s = Score([new VoteInput("v1", 'Y', 5), new VoteInput("v2", 'N', 5)], new Dictionary<string, char> { ["v2"] = 'N' });
        s.Score.Should().Be(100);
        s.Contributions.Single(c => c.VoteId == "v1").Counted.Should().BeFalse();
    }

    [Fact(DisplayName = "A state with no counted vote has no score (n/a)")]
    public void NoCountedVotes_NoScore()
    {
        var s = Score([new VoteInput("v1", 'Y', 5)], new Dictionary<string, char> { ["v1"] = 'A' });
        s.Score.Should().BeNull();
    }

    [Fact(DisplayName = "Appendix B example: Brazil 100 × 9 / 11 = 81.8")]
    public void AppendixExample()
    {
        var s = Score(
            [new VoteInput("101", 'Y', 4), new VoteInput("102", 'Y', 2), new VoteInput("103", 'N', 3), new VoteInput("104", 'Y', 5)],
            new Dictionary<string, char> { ["101"] = 'Y', ["102"] = 'N', ["103"] = 'A', ["104"] = 'Y' });
        Math.Round(s.Score!.Value, 1).Should().Be(81.8);
    }
}
