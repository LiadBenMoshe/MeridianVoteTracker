using System.Diagnostics;
using FluentAssertions;
using Meridian.Api.Contracts;
using Meridian.Core.Domain;
using Xunit.Abstractions;

namespace Meridian.IntegrationTests;

/// <summary>
/// Scale: ~1M-row historic file in the background, then a snapshot over the full history (SNP-02).
/// Slow (about a minute), so it only runs with MERIDIAN_SCALE_TESTS=1.
/// </summary>
[Collection("api")]
public sealed class ScaleTests(ApiFactory app, ITestOutputHelper output) : IAsyncLifetime
{
    public Task InitializeAsync() => app.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact(DisplayName = "Scale: historic import (~1M rows) + full-history snapshot")]
    public async Task Historic_And_FullSnapshot()
    {
        Skip.If(Environment.GetEnvironmentVariable("MERIDIAN_SCALE_TESTS") != "1", "set MERIDIAN_SCALE_TESTS=1");
        var dir = RealDataTests.DataDir();
        Skip.If(dir is null, "assignment data folder not found");

        var dana = app.As("dana");
        var sw = Stopwatch.StartNew();
        var r = await dana.PostFile("/api/imports", "assembly_historic.csv", await File.ReadAllBytesAsync(Path.Combine(dir, "assembly_historic.csv")), "text/csv");
        var accepted = sw.Elapsed;
        var import = await dana.WaitImport((await r.Read<ImportDto>()).Id, 900);
        output.WriteLine($"Upload accepted after {accepted.TotalSeconds:F1}s, import done after {sw.Elapsed.TotalSeconds:F1}s: {import.Counts}");
        import.Status.Should().Be(ImportStatus.CompletedWithIssues);
        import.TotalRows.Should().Be(962171);

        var bulk = await (await app.As("lena").PostJson("/api/votes/positions/bulk", new { position = "Y", weight = 3, onlyWithoutPosition = true })).Read<BulkPositionResultDto>();
        output.WriteLine($"Positions set on {bulk.Updated} votes");

        sw.Restart();
        var s = await dana.PostJson("/api/snapshots", new { name = "History 2015–2023", selection = new { from = "2015-01-01", to = "2023-12-31" } });
        var snapshot = await dana.WaitSnapshot((await s.Read<SnapshotDto>()).Id, 900);
        output.WriteLine($"Full-history snapshot of {snapshot.Snapshot.VoteCount} votes in {sw.Elapsed.TotalSeconds:F1}s");
        snapshot.Snapshot.Status.Should().Be(JobStatus.Completed);
        snapshot.RankedCount.Should().BeGreaterThan(100);
    }
}
