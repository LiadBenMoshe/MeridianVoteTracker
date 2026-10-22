using System.Net;
using FluentAssertions;
using Meridian.Api.Contracts;
using Meridian.Core.Domain;
using Meridian.Core.Extraction;
using Microsoft.EntityFrameworkCore;

namespace Meridian.IntegrationTests;

[Collection("api")]
public sealed class ExtractionTests(ApiFactory app) : IAsyncLifetime
{
    public Task InitializeAsync() => app.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static LayoutCell C(string t, double c = 0.99) => new(t, c);

    private static LayoutDocument Layout(double japanConfidence) => new(
        ["Vote No.: MA/2011/042\nSession: 66\nDate of vote: 4 October 2011\nResolution: Protection of Cultural Heritage\nRecorded totals: Yes: 2 No: 1 Abstentions: 0 Not voting: 0"],
        [new LayoutTable(1,
        [
            [C("Code"), C("Member state"), C("YES"), C("NO"), C("ABST.")],
            [C("BRA"), C("Brazil"), C("X"), C(""), C("")],
            [C("IND"), C("India"), C("X"), C(""), C("")],
            [C("JPN"), C("Japan"), C(""), C("X", japanConfidence), C("")],
        ])]);

    private async Task<ExtractionDto> Upload(HttpClient c)
    {
        var r = await c.PostFile("/api/extractions", "MA_2011_042.pdf", "%PDF-1.4 fake"u8.ToArray(), "application/pdf");
        r.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return await r.Read<ExtractionDto>();
    }

    [Fact(DisplayName = "PDF-01..05: extraction runs in background, issues are flagged, approval is blocked until fixed, then goes through import rules")]
    public async Task Extraction_ReviewFlow()
    {
        app.Layout.Next = () => Layout(japanConfidence: 0.41);
        var dana = app.As("dana");
        var e = await dana.WaitExtraction((await Upload(dana)).Id, x => x.Status == ExtractionStatus.NeedsReview);

        e.Sheet!.VoteId.Should().Be("MA/2011/042");
        e.Sheet.Rows.Single(r => r.Code == "JPN").Issues.Should().Contain("Low confidence");
        e.Sheet.Issues.Should().Contain(i => i.StartsWith("Member state(s) missing"), "other members of 2011 are not on this test sheet");
        (await app.WithDb(db => db.CountryVotes.CountAsync())).Should().Be(0, "nothing extracted is approved data before review (PDF-05)");

        var blocked = await dana.PostJson($"/api/extractions/{e.Id}/approve", new { version = e.Version });
        blocked.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // The analyst checks Japan on the scan and confirms it (confidence = 1).
        var sheet = e.Sheet;
        sheet.Rows.Single(r => r.Code == "JPN").Confidence = 1.0;
        var updated = await (await dana.PutJson($"/api/extractions/{e.Id}", new { sheet, version = e.Version })).Read<ExtractionDto>();
        updated.Sheet!.Rows.Single(r => r.Code == "JPN").Issues.Should().BeEmpty();

        // Stale version → 409 (two analysts editing the same extraction).
        (await dana.PutJson($"/api/extractions/{e.Id}", new { sheet, version = e.Version })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "PDF-05: an approved extraction is imported with the same rules (new / unchanged / conflict)")]
    public async Task Approved_IsImported()
    {
        // A sheet that passes every check: all members of that date, totals matching.
        var members = await app.WithDb(db => db.Memberships.Where(m => m.From <= new DateOnly(2011, 10, 4) && (m.To == null || m.To >= new DateOnly(2011, 10, 4)))
            .Select(m => m.CountryCode).Distinct().OrderBy(c => c).ToListAsync());
        var rows = new List<IReadOnlyList<LayoutCell>> { new[] { C("Code"), C("Member state"), C("YES"), C("NO"), C("ABST.") } };
        rows.AddRange(members.Select(code => (IReadOnlyList<LayoutCell>)new[] { C(code), C(code), C("X"), C(""), C("") }));
        app.Layout.Next = () => new LayoutDocument(
            [$"Vote No.: MA/2011/042\nSession: 66\nDate of vote: 4 October 2011\nResolution: Heritage\nRecorded totals: Yes: {members.Count} No: 0 Abstentions: 0 Not voting: 0"],
            [new LayoutTable(1, rows)]);

        var dana = app.As("dana");
        var e = await dana.WaitExtraction((await Upload(dana)).Id, x => x.Status == ExtractionStatus.NeedsReview);
        e.IssueCount.Should().Be(0);

        var approved = await (await dana.PostJson($"/api/extractions/{e.Id}/approve", new { version = e.Version })).Read<ExtractionDto>();
        approved.Status.Should().Be(ExtractionStatus.Approved);
        var import = await dana.WaitImport(approved.ImportId!.Value);
        import.Source.Should().Be(ImportSource.PdfExtraction);
        import.Counts.New.Should().Be(members.Count);

        // Same sheet again → unchanged, nothing new (same rules as CSV).
        var e2 = await dana.WaitExtraction((await Upload(dana)).Id, x => x.Status == ExtractionStatus.NeedsReview);
        var a2 = await (await dana.PostJson($"/api/extractions/{e2.Id}/approve", new { version = e2.Version })).Read<ExtractionDto>();
        (await dana.WaitImport(a2.ImportId!.Value)).Counts.Unchanged.Should().Be(members.Count);
    }

    [Fact(DisplayName = "PDF-06: AI failures are shown, retried, end as Failed (never stuck), and can be retried by the user")]
    public async Task Failures_AreVisible_AndRetryable()
    {
        app.Layout.Next = () => throw new HttpRequestException("Service unavailable (503)");
        var dana = app.As("dana");
        var failed = await dana.WaitExtraction((await Upload(dana)).Id, x => x.Status == ExtractionStatus.Failed);
        failed.Attempts.Should().Be(3);
        failed.Error.Should().Contain("503");

        app.Layout.Next = () => Layout(0.99);
        var retried = await (await dana.PostAsync($"/api/extractions/{failed.Id}/retry", null)).Read<ExtractionDto>();
        retried.Status.Should().Be(ExtractionStatus.Queued);
        (await dana.WaitExtraction(failed.Id, x => x.Status == ExtractionStatus.NeedsReview)).Error.Should().BeNull();
    }
}

/// <summary>The real assignment files (skipped when the data folder is not next to the repo).</summary>
[Collection("api")]
public sealed class RealDataTests(ApiFactory app) : IAsyncLifetime
{
    public Task InitializeAsync() => app.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    public static string? DataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Assignment-DotNet");
            if (File.Exists(Path.Combine(candidate, "assembly_export_2024_q1.csv"))) return candidate;
            dir = dir.Parent;
        }
        return Environment.GetEnvironmentVariable("MERIDIAN_DATA_DIR");
    }

    private async Task<ImportDto> Upload(HttpClient c, string file)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(DataDir()!, file));
        var r = await c.PostFile("/api/imports", file, bytes, "text/csv");
        return await c.WaitImport((await r.Read<ImportDto>()).Id, 600);
    }

    [SkippableFact(DisplayName = "Real Q1 files: issues found in q1; rev2 opens exactly the 8 real changes; re-upload is quiet")]
    public async Task Q1_Then_Rev2()
    {
        Skip.If(DataDir() is null, "assignment data folder not found");
        var dana = app.As("dana");

        var q1 = await Upload(dana, "assembly_export_2024_q1.csv");
        q1.Status.Should().Be(ImportStatus.CompletedWithIssues);
        q1.TotalRows.Should().Be(5896);
        q1.Counts.Invalid.Should().Be(11);      // FRN, 'Yes', empty ballot, 'AGO;N' (7 columns), 6× 21/02/2024, NZL after leaving ('deu' is normalised, not invalid)
        q1.Counts.Duplicate.Should().Be(3);
        q1.Counts.Contradiction.Should().Be(151); // FRA ×2 in 012 + all 149 rows of 025 (title differs)

        var rev2 = await Upload(dana, "assembly_export_2024_q1_rev2.csv");
        rev2.Counts.ConflictsCreated.Should().Be(8); // 6 country-vote changes (003 BRA/IND, 010 JPN, 019 ZAF, 028 CAN/MEX) + title of 009 + date of 014
        rev2.Counts.Invalid.Should().Be(0);

        var again = await Upload(dana, "assembly_export_2024_q1_rev2.csv");
        again.Counts.New.Should().Be(0);
        again.Counts.ConflictsCreated.Should().Be(0);

        var v40 = await dana.GetJson<VoteDetailDto>("/api/votes/detail?id=" + Uri.EscapeDataString("MA/2024/040"));
        v40.CountryVotes.Should().NotBeEmpty("a vote missing from rev2 is not deleted (ING-09)");
    }
}
