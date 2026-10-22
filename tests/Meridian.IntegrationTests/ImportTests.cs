using System.Net;
using FluentAssertions;
using Meridian.Api.Contracts;
using Meridian.Core.Domain;
using Microsoft.EntityFrameworkCore;
using static Meridian.IntegrationTests.Http;

namespace Meridian.IntegrationTests;

/// <summary>Section 7.1 / 7.2 end to end: HTTP → background worker → PostgreSQL.</summary>
[Collection("api")]
public sealed class ImportTests(ApiFactory app) : IAsyncLifetime
{
    public Task InitializeAsync() => app.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    public static string[] Original =>
    [
        Row("MA/2024/101", "BRA", "Y"),
        Row("MA/2024/101", "IND", "Y"),
        Row("MA/2024/102", "JPN", "Y", "Food Security"),
        Row("MA/2024/102", "JPN", "Y", "Food Security"),
        Row("MA/2024/103", "FRA", "Y", "Protection of Cultural Heritage", "2024-01-18"),
        Row("MA/2024/103", "FRA", "N", "Protection of Cultural Heritage", "2024-01-18"),
        Row("MA/2024/104", "FRN", "X", "Ocean Plastic Pollution", "2024-02-06"),
        Row("MA/2024/104", "NZL", "Y", "Ocean Plastic Pollution", "2024-02-06"),
    ];

    public static string[] Revised =>
    [
        Row("MA/2024/101", "BRA", "A", "Maritime Safety: Revised", published: "2024-04-20T09:00:00Z"),
        Row("MA/2024/101", "IND", "Y", "Maritime Safety: Revised", published: "2024-04-20T09:00:00Z"),
        Row("MA/2024/103", "FRA", "N", "Protection of Cultural Heritage", "2024-01-18", "2024-04-20T09:00:00Z"),
        Row("MA/2024/104", "FRA", "X", "Ocean Plastic Pollution", "2024-02-06", "2024-04-20T09:00:00Z"),
        Row("MA/2024/105", "BRA", "Y", "Climate Adaptation Finance", "2024-03-05", "2024-04-20T09:00:00Z"),
    ];

    [Fact(DisplayName = "ING-01: upload returns 202 immediately; processing continues in the background")]
    public async Task Upload_ReturnsImmediately()
    {
        var dana = app.As("dana");
        var r = await dana.UploadCsv("example.csv", Original);

        r.StatusCode.Should().Be(HttpStatusCode.Accepted);
        r.Headers.Location.Should().NotBeNull();
        var dto = await r.Read<ImportDto>();
        dto.Status.Should().BeOneOf(ImportStatus.Queued, ImportStatus.Processing);
        dto.UploadedBy.Should().Be("dana");
        (await dana.WaitImport(dto.Id)).Status.Should().Be(ImportStatus.CompletedWithIssues);
    }

    [Fact(DisplayName = "ING-02: progress is stored on the server (rows processed of total), so a page refresh does not lose it")]
    public async Task Progress_IsPersisted()
    {
        var dana = app.As("dana");
        var done = await dana.ImportAndWait("example.csv", Original);

        done.TotalRows.Should().Be(8);
        done.ProcessedRows.Should().Be(8);
        done.Percent.Should().Be(100);
        // A different client (= a refreshed page) reads the same progress.
        (await app.As("omar").GetJson<ImportDto>($"/api/imports/{done.Id}")).ProcessedRows.Should().Be(8);
    }

    [Fact(DisplayName = "7.2 original upload: counts per class and rows not stored (ING-04..07, ING-14)")]
    public async Task OriginalFile_Report()
    {
        var dana = app.As("dana");
        var import = await dana.ImportAndWait("example.csv", Original);

        import.Counts.Should().BeEquivalentTo(new ImportCountsDto(New: 3, Unchanged: 0, Duplicate: 1, Invalid: 2, Contradiction: 2, Conflict: 0, ConflictsCreated: 0));

        var report = await dana.GetJson<ImportReportDto>($"/api/imports/{import.Id}/report");
        report.Votes.Select(v => v.VoteId).Should().Equal("MA/2024/101", "MA/2024/102", "MA/2024/103", "MA/2024/104");
        report.ProblemRows.Should().HaveCount(5);
        report.ProblemRows.Single(r => r.RowNo == 5).Reason.Should().Be("Identical to row 4");
        report.ProblemRows.Single(r => r.RowNo == 8).Reason.Should().Contain("Unknown country code 'FRN'");
        report.ProblemRows.Single(r => r.RowNo == 9).Reason.Should().Contain("not a member on 2024-02-06");
        report.ProblemRows.Single(r => r.RowNo == 9).Raw.Should().Be(Original[7], "the original value is reported");
        report.Normalization.Rules.Should().NotBeEmpty("normalisation rules are shown in the report (ING-13)");
    }

    [Fact(DisplayName = "7.2 corrected upload: conflicts, unchanged, completions, new vote; approved data untouched (ING-08..10)")]
    public async Task RevisedFile_Report()
    {
        var dana = app.As("dana");
        await dana.ImportAndWait("example.csv", Original);
        var rev = await dana.ImportAndWait("example_rev2.csv", Revised);

        rev.Counts.Should().BeEquivalentTo(new ImportCountsDto(New: 3, Unchanged: 1, Duplicate: 0, Invalid: 0, Contradiction: 0, Conflict: 1, ConflictsCreated: 2));

        // ING-08: Brazil is still YES in vote 101 until a human decides.
        var v101 = await dana.GetJson<VoteDetailDto>($"/api/votes/detail?id={Q("MA/2024/101")}");
        v101.CountryVotes.Single(c => c.Code == "BRA").Ballot.Should().Be("Y");
        v101.Title.Should().Be("Maritime Safety");
        v101.PendingConflicts.Should().Be(2);

        // ING-09: vote 102 is not in the file and is unchanged.
        var v102 = await dana.GetJson<VoteDetailDto>($"/api/votes/detail?id={Q("MA/2024/102")}");
        v102.CountryVotes.Should().ContainSingle(c => c.Code == "JPN" && c.Ballot == "Y");

        // ING-10: France completes votes 103 and 104 as new data.
        (await dana.GetJson<VoteDetailDto>($"/api/votes/detail?id={Q("MA/2024/104")}")).CountryVotes.Should().ContainSingle(c => c.Code == "FRA" && c.Ballot == "X");
    }

    [Fact(DisplayName = "ING-12: uploading the same corrected file again gives 0 new rows and 0 new conflicts")]
    public async Task SameFileAgain_NoDuplicates()
    {
        var dana = app.As("dana");
        await dana.ImportAndWait("example.csv", Original);
        await dana.ImportAndWait("example_rev2.csv", Revised);
        var again = await dana.ImportAndWait("example_rev2.csv", Revised);

        again.Counts.New.Should().Be(0);
        again.Counts.ConflictsCreated.Should().Be(0);
        again.SameContentAsImportId.Should().NotBeNull();
        (await app.WithDb(db => db.Conflicts.CountAsync())).Should().Be(2);
        (await app.WithDb(db => db.CountryVotes.CountAsync())).Should().Be(6);
    }

    [Fact(DisplayName = "HIS-01 / ING-15: every import keeps its file, uploader, time, report and per-row outcome")]
    public async Task ImportHistory_IsKept()
    {
        var dana = app.As("dana");
        var import = await dana.ImportAndWait("example.csv", Original);

        var list = await dana.GetJson<PagedResult<ImportDto>>("/api/imports");
        list.Items.Should().ContainSingle(i => i.Id == import.Id && i.UploadedBy == "dana" && i.FileName == "example.csv");

        var rows = await dana.GetJson<PagedResult<ImportRowDto>>($"/api/imports/{import.Id}/rows?pageSize=100");
        rows.Total.Should().Be(8, "every row has an outcome");
        rows.Items.Select(r => r.Outcome).Should().Contain([RowOutcome.New, RowOutcome.Duplicate, RowOutcome.Contradiction, RowOutcome.Invalid]);

        var file = await dana.GetStringAsync($"/api/imports/{import.Id}/file");
        file.Should().Contain(Original[0]);
    }

    [Fact(DisplayName = "ING-07: a broken line does not stop the file; a missing header fails clearly")]
    public async Task BrokenLines()
    {
        var dana = app.As("dana");
        var ok = await dana.ImportAndWait("broken.csv", ["MA/2024/201,79,\"unterminated", Row("MA/2024/201", "BRA", "Y")]);
        ok.Counts.Invalid.Should().Be(1);
        ok.Counts.New.Should().Be(1);

        var r = await dana.PostFile("/api/imports", "noheader.csv", "a,b\n1,2\n"u8.ToArray(), "text/csv");
        var failed = await dana.WaitImport((await r.Read<ImportDto>()).Id);
        failed.Status.Should().Be(ImportStatus.Failed);
        failed.Error.Should().Contain("Missing column(s)");
    }

    [Fact(DisplayName = "Roles: an unknown/missing user gets 401 ProblemDetails with a correlation id")]
    public async Task Unauthenticated_IsProblemDetails()
    {
        var r = await app.CreateClient().GetAsync("/api/imports");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var p = await r.Read<Dictionary<string, object>>();
        p.Should().ContainKey("title").And.ContainKey("correlationId");
    }
}
