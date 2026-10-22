using System.Net;
using System.Text.Json;
using FluentAssertions;
using Meridian.Api.Contracts;
using Meridian.Core.Domain;
using static Meridian.IntegrationTests.Http;

namespace Meridian.IntegrationTests;

/// <summary>Sections 7.5 (positions and weights) and 7.6 (snapshots).</summary>
[Collection("api")]
public sealed class PositionAndSnapshotTests(ApiFactory app) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await app.ResetAsync();
        var dana = app.As("dana");
        await dana.ImportAndWait("example.csv", ImportTests.Original);
        await dana.ImportAndWait("example_rev2.csv", ImportTests.Revised);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Pos(string id) => $"/api/votes/position?id={Q(id)}";

    private async Task SetAllPositions()
    {
        var lena = app.As("lena");
        (await lena.PutJson(Pos("MA/2024/101"), new { position = "Y", weight = 4 })).EnsureSuccessStatusCode();
        (await lena.PutJson(Pos("MA/2024/102"), new { position = "Y", weight = 2 })).EnsureSuccessStatusCode();
        (await lena.PutJson(Pos("MA/2024/103"), new { position = "N", weight = 3 })).EnsureSuccessStatusCode();
        (await lena.PutJson(Pos("MA/2024/104"), new { position = "Y", weight = 5 })).EnsureSuccessStatusCode();
        (await lena.PutJson(Pos("MA/2024/105"), new { position = "Y", weight = 1 })).EnsureSuccessStatusCode();
    }

    private async Task<SnapshotRankingDto> CreateSnapshot(HttpClient c, string name)
    {
        var r = await c.PostJson("/api/snapshots", new { name, selection = new { from = "2024-01-01", to = "2024-03-31" } });
        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        return await c.WaitSnapshot((await r.Read<SnapshotDto>()).Id);
    }

    [Fact(DisplayName = "POS-01: only the admin can set a position; an analyst gets 403 from the API")]
    public async Task Position_AdminOnly()
    {
        var r = await app.As("dana").PutJson(Pos("MA/2024/101"), new { position = "Y", weight = 4 });
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await r.Read<JsonElement>()).GetProperty("title").GetString().Should().Be("Not allowed");

        (await app.As("lena").PutJson(Pos("MA/2024/101"), new { position = "Y", weight = 4 })).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact(DisplayName = "POS-02: weight 7 → 400 with the error on the Weight field")]
    public async Task Position_ValidationPerField()
    {
        var r = await app.As("lena").PutJson(Pos("MA/2024/101"), new { position = "Y", weight = 7 });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await r.Read<JsonElement>()).GetProperty("errors");
        errors.GetProperty("Weight")[0].GetString().Should().Be("Weight must be between 1 (minor) and 5 (critical).");
    }

    [Fact(DisplayName = "POS-03 / HIS-03: each change records who, when, old value and new value")]
    public async Task Position_Audited()
    {
        var lena = app.As("lena");
        await lena.PutJson(Pos("MA/2024/101"), new { position = "Y", weight = 4 });
        await lena.PutJson(Pos("MA/2024/101"), new { position = "Y", weight = 2 });

        var t = await lena.GetJson<List<TimelineEventDto>>($"/api/votes/timeline?id={Q("MA/2024/101")}");
        var weight = t.Where(e => e.Type == "PositionChanged" && e.Field == "weight").ToList();
        weight.Should().HaveCount(2);
        weight[0].OldValue.Should().Be("4");
        weight[0].NewValue.Should().Be("2");
        weight[0].Actor.Should().Be("lena");
    }

    [Fact(DisplayName = "POS-04: a vote without position blocks snapshot creation (422 listing the votes); the list flags it")]
    public async Task NoPosition_BlocksSnapshot()
    {
        var lena = app.As("lena");
        await lena.PutJson(Pos("MA/2024/101"), new { position = "Y", weight = 4 });

        var r = await app.As("dana").PostJson("/api/snapshots", new { name = "Q1", selection = new { from = "2024-01-01", to = "2024-03-31" } });
        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var p = await r.Read<JsonElement>();
        p.GetProperty("missingPositions").EnumerateArray().Select(x => x.GetString()).Should().Contain("MA/2024/105");

        var list = await lena.GetJson<VoteListDto>("/api/votes");
        list.WithoutPositionTotal.Should().Be(4);
    }

    [Fact(DisplayName = "SNP-01..03: snapshot by date range runs in the background; score 0–100, rank, per-vote breakdown with rules")]
    public async Task Snapshot_Calculates()
    {
        await SetAllPositions();
        var s = await CreateSnapshot(app.As("dana"), "2024-Q1");

        s.Snapshot.Status.Should().Be(JobStatus.Completed);
        s.Snapshot.VoteCount.Should().Be(5);
        s.Snapshot.Rules.Should().Contain(r => r.Contains("ABSTAIN"));

        // BRA: 101 Y (wanted Y, w4) + 105 Y (wanted Y, w1) → 100. IND: 101 Y → 100. JPN: 102 Y (Y, w2) → 100.
        // FRA: 103 N (wanted N, w3) matched; 104 X not counted → 100.
        s.Scores.Single(x => x.Code == "BRA").Score.Should().Be(100);
        s.Scores.Single(x => x.Code == "DEU").Score.Should().BeNull("no counted votes → n/a");
        s.Scores.Single(x => x.Code == "DEU").Rank.Should().BeNull();

        var bra = await app.As("dana").GetJson<CountryBreakdownDto>($"/api/snapshots/{s.Snapshot.Id}/countries/BRA");
        bra.Contributions.Should().HaveCount(5);
        bra.Contributions.Single(c => c.VoteId == "MA/2024/101").Should().Match<ContributionDto>(c => c.Counted && c.Matched && c.Weight == 4);
        bra.Contributions.Single(c => c.VoteId == "MA/2024/102").Note.Should().Contain("not a member");
        bra.Rules.Should().NotBeEmpty();
    }

    [Fact(DisplayName = "SNP-04: a saved snapshot never changes after conflict decisions or weight changes; a new one uses the new data")]
    public async Task Snapshot_IsImmutable()
    {
        await SetAllPositions();
        var dana = app.As("dana");
        var first = await CreateSnapshot(dana, "2024-Q1");
        var before = JsonSerializer.Serialize(first.Scores);

        // Brazil YES → ABSTAIN accepted, and the weight of 101 changed.
        var conflict = (await dana.GetJson<PagedResult<ConflictDto>>($"/api/conflicts?voteId={Q("MA/2024/101")}")).Items.Single(c => c.Kind == ConflictKind.CountryVote);
        (await dana.PostJson($"/api/conflicts/{conflict.Id}/decision", new { decision = "Accept" })).EnsureSuccessStatusCode();
        (await app.As("lena").PutJson(Pos("MA/2024/105"), new { position = "N", weight = 5 })).EnsureSuccessStatusCode();

        var reread = await dana.GetJson<SnapshotRankingDto>($"/api/snapshots/{first.Snapshot.Id}");
        JsonSerializer.Serialize(reread.Scores).Should().Be(before);
        var breakdown = await dana.GetJson<CountryBreakdownDto>($"/api/snapshots/{first.Snapshot.Id}/countries/BRA");
        breakdown.Contributions.Single(c => c.VoteId == "MA/2024/101").Ballot.Should().Be("Y", "the snapshot keeps the value approved when it was created");
        breakdown.Contributions.Single(c => c.VoteId == "MA/2024/101").CurrentBallot.Should().Be("A");

        var second = await CreateSnapshot(dana, "2024-Q1 (after corrections)");
        second.Scores.Single(x => x.Code == "BRA").Score.Should().Be(0, "now 101 is ABSTAIN (not counted) and 105 YES vs wanted NO");
    }

    [Fact(DisplayName = "SNP-05: only the admin publishes; one published snapshot per quarter")]
    public async Task Publish_OnePerQuarter()
    {
        await SetAllPositions();
        var a = await CreateSnapshot(app.As("dana"), "A");
        var b = await CreateSnapshot(app.As("dana"), "B");

        (await app.As("dana").PostJson($"/api/snapshots/{a.Snapshot.Id}/publish", new { quarter = "2024-Q1" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var lena = app.As("lena");
        var ok = await lena.PostJson($"/api/snapshots/{a.Snapshot.Id}/publish", new { quarter = "2024-Q1" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Read<SnapshotDto>()).PublishedBy.Should().Be("lena");

        var dup = await lena.PostJson($"/api/snapshots/{b.Snapshot.Id}/publish", new { quarter = "2024-Q1" });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await dup.Read<JsonElement>()).GetProperty("title").GetString().Should().Be("Quarter already published");

        (await lena.PostJson($"/api/snapshots/{b.Snapshot.Id}/publish", new { quarter = "Q1-2024" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var t = await lena.GetJson<List<TimelineEventDto>>($"/api/votes/timeline?id={Q("MA/2024/101")}");
        t.Should().Contain(e => e.Type == "SnapshotPublished" && e.SnapshotId == a.Snapshot.Id);
    }

    [Fact(DisplayName = "SNP-01: selection by session and by explicit list")]
    public async Task Selection_SessionAndList()
    {
        await SetAllPositions();
        var dana = app.As("dana");
        var p1 = await (await dana.PostJson("/api/snapshots/preview", new { session = 79 })).Read<SnapshotPreviewDto>();
        p1.VoteCount.Should().Be(5);
        var p2 = await (await dana.PostJson("/api/snapshots/preview", new { voteIds = new[] { "MA/2024/101", "MA/2024/999" } })).Read<SnapshotPreviewDto>();
        p2.VoteCount.Should().Be(1);
        p2.UnknownVoteIds.Should().Equal("MA/2024/999");
    }
}
