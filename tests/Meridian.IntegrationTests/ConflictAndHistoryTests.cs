using System.Net;
using System.Text.Json;
using FluentAssertions;
using Meridian.Api.Contracts;
using Meridian.Core.Domain;
using Microsoft.EntityFrameworkCore;
using static Meridian.IntegrationTests.Http;

namespace Meridian.IntegrationTests;

/// <summary>Sections 7.3 (conflicts) and 7.4 (history).</summary>
[Collection("api")]
public sealed class ConflictAndHistoryTests(ApiFactory app) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await app.ResetAsync();
        var dana = app.As("dana");
        await dana.ImportAndWait("example.csv", ImportTests.Original);
        await dana.ImportAndWait("example_rev2.csv", ImportTests.Revised);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ConflictDto> BrazilConflict(HttpClient c) =>
        (await c.GetJson<PagedResult<ConflictDto>>($"/api/conflicts?voteId={Q("MA/2024/101")}")).Items.Single(x => x.Kind == ConflictKind.CountryVote);

    [Fact(DisplayName = "CON-01/02/03: pending list filtered by vote; approved vs incoming with their sources; country and detail conflicts")]
    public async Task List_And_Detail()
    {
        var dana = app.As("dana");
        var list = await dana.GetJson<PagedResult<ConflictDto>>($"/api/conflicts?voteId={Q("MA/2024/101")}");
        list.Items.Should().HaveCount(2);
        list.Items.Should().ContainSingle(c => c.Kind == ConflictKind.VoteDetail && c.Field == VoteFields.Title && c.IncomingValue == "Maritime Safety: Revised");

        var c = await dana.GetJson<ConflictDto>($"/api/conflicts/{(await BrazilConflict(dana)).Id}");
        c.ApprovedValue.Should().Be("Y");
        c.IncomingValue.Should().Be("A");
        c.ApprovedSource!.FileName.Should().Be("example.csv");
        c.IncomingSource.FileName.Should().Be("example_rev2.csv");
        c.IncomingSource.UploadedBy.Should().Be("dana");
        c.IncomingSource.UploadedAt.Should().NotBeNull();

        (await dana.GetJson<PagedResult<ConflictDto>>($"/api/conflicts?voteId={Q("MA/2024/999")}")).Items.Should().BeEmpty();
    }

    [Fact(DisplayName = "CON-04 / CON-06 / HIS-02..04: accept → new approved value; old value kept in the timeline; decision visible afterwards")]
    public async Task Accept_UpdatesApproved_AndKeepsHistory()
    {
        var dana = app.As("dana");
        var id = (await BrazilConflict(dana)).Id;

        var r = await dana.PostJson($"/api/conflicts/{id}/decision", new { decision = "Accept", comment = "Corrigendum of 18 April" });
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var decided = await r.Read<ConflictDto>();
        decided.Status.Should().Be(ConflictStatus.Accepted);
        decided.DecidedBy.Should().Be("dana");
        decided.Comment.Should().Be("Corrigendum of 18 April");

        var vote = await dana.GetJson<VoteDetailDto>($"/api/votes/detail?id={Q("MA/2024/101")}");
        var bra = vote.CountryVotes.Single(x => x.Code == "BRA");
        bra.Ballot.Should().Be("A");
        bra.ChangedByDecision.Should().BeTrue();
        bra.ApprovedBy.Should().Be("dana");
        bra.SourceFile.Should().Be("example_rev2.csv");

        var timeline = await dana.GetJson<List<TimelineEventDto>>($"/api/votes/timeline?id={Q("MA/2024/101")}");
        var accepted = timeline.Single(e => e.Type == "ConflictAccepted");
        accepted.OldValue.Should().Be("Y");
        accepted.NewValue.Should().Be("A");
        accepted.Actor.Should().Be("dana");
        accepted.ImportFile.Should().Be("example_rev2.csv");
        timeline.Should().Contain(e => e.Type == "VoteCreated").And.Contain(e => e.Type == "ConflictsCreated");

        var resolved = await dana.GetJson<PagedResult<ConflictDto>>("/api/conflicts?resolved=true");
        resolved.Items.Should().ContainSingle(x => x.Id == id && x.Status == ConflictStatus.Accepted);
    }

    [Fact(DisplayName = "CON-04: reject keeps the approved value; title conflict accept changes vote details")]
    public async Task Reject_And_DetailAccept()
    {
        var dana = app.As("dana");
        var all = (await dana.GetJson<PagedResult<ConflictDto>>($"/api/conflicts?voteId={Q("MA/2024/101")}")).Items;
        (await dana.PostJson($"/api/conflicts/{all.Single(c => c.Kind == ConflictKind.CountryVote).Id}/decision", new { decision = "Reject" })).EnsureSuccessStatusCode();
        (await dana.PostJson($"/api/conflicts/{all.Single(c => c.Kind == ConflictKind.VoteDetail).Id}/decision", new { decision = "Accept" })).EnsureSuccessStatusCode();

        var vote = await dana.GetJson<VoteDetailDto>($"/api/votes/detail?id={Q("MA/2024/101")}");
        vote.CountryVotes.Single(x => x.Code == "BRA").Ballot.Should().Be("Y");
        vote.Title.Should().Be("Maritime Safety: Revised");
    }

    [Fact(DisplayName = "CON-05: two analysts decide the same conflict at the same moment → exactly one wins, the other gets 409 with the current state")]
    public async Task ConcurrentDecisions_OneWins()
    {
        var id = (await BrazilConflict(app.As("dana"))).Id;
        var dana = app.As("dana");
        var omar = app.As("omar");

        var results = await Task.WhenAll(
            dana.PostJson($"/api/conflicts/{id}/decision", new { decision = "Accept" }),
            omar.PostJson($"/api/conflicts/{id}/decision", new { decision = "Reject" }));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        var loser = results.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problem = await loser.Read<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("Already decided");
        problem.GetProperty("detail").GetString().Should().Contain("was not applied");
        problem.GetProperty("current").GetProperty("status").GetString().Should().BeOneOf("Accepted", "Rejected");

        (await app.WithDb(db => db.VoteEvents.CountAsync(e => e.ConflictId == id && (e.Type == "ConflictAccepted" || e.Type == "ConflictRejected"))))
            .Should().Be(1, "only one decision is recorded as applied");
    }

    [Fact(DisplayName = "HIS-02 / ING-08: approved data cannot be overwritten or history deleted, even with SQL (DB triggers)")]
    public async Task Database_Guards()
    {
        var act = () => app.WithDb(db => db.Database.ExecuteSqlRawAsync("DELETE FROM vote_events"));
        await act.Should().ThrowAsync<Exception>().WithMessage("*append-only*");

        var conflictId = (await BrazilConflict(app.As("dana"))).Id;
        await app.As("dana").PostJson($"/api/conflicts/{conflictId}/decision", new { decision = "Reject" });
        var change = () => app.WithDb(db => db.Database.ExecuteSqlRawAsync($"UPDATE conflicts SET status = 'Accepted' WHERE id = {conflictId}"));
        await change.Should().ThrowAsync<Exception>().WithMessage("*already decided*");
    }

    [Fact(DisplayName = "After accept, re-uploading either file does not reopen the decided question (ING-12)")]
    public async Task AfterDecision_ReuploadIsQuiet()
    {
        var dana = app.As("dana");
        foreach (var c in (await dana.GetJson<PagedResult<ConflictDto>>("/api/conflicts")).Items)
            (await dana.PostJson($"/api/conflicts/{c.Id}/decision", new { decision = "Accept" })).EnsureSuccessStatusCode();

        (await dana.ImportAndWait("example_rev2.csv", ImportTests.Revised)).Counts.ConflictsCreated.Should().Be(0);
        (await dana.ImportAndWait("example.csv", ImportTests.Original)).Counts.ConflictsCreated.Should().Be(0);
    }
}
