using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Api.Contracts;
using Meridian.Core.Domain;
using Meridian.Core.Extraction;
using Meridian.Infrastructure.Data;
using Meridian.Infrastructure.Extraction;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Meridian.IntegrationTests;

/// <summary>The real API + background workers against a real PostgreSQL in Docker.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private readonly string _files = Path.Combine(Path.GetTempPath(), "meridian-tests-" + Guid.NewGuid().ToString("N"));

    public FakeLayoutReader Layout { get; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task InitializeAsync() => await _pg.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync(); // stop the app and its workers before the database goes away
        await _pg.DisposeAsync();
        try { Directory.Delete(_files, true); } catch (IOException) { }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Meridian", _pg.GetConnectionString());
        builder.UseSetting("Storage:Provider", "Local");
        builder.UseSetting("Storage:LocalPath", _files);
        builder.UseSetting("DocumentIntelligence:RetryDelaySeconds", "0");
        builder.ConfigureTestServices(s => s.AddSingleton<ILayoutReader>(Layout));
    }

    public HttpClient As(string user)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-User", user);
        return c;
    }

    /// <summary>Clean business data between tests (base data stays). TRUNCATE bypasses the append-only row triggers.</summary>
    public async Task ResetAsync()
    {
        _ = Server; // make sure the app (and migrations) started
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE vote_events, import_rows, import_votes, snapshot_scores, snapshot_ballots, snapshot_votes, snapshots,
                     conflicts, country_votes, votes, extractions, imports RESTART IDENTITY CASCADE
            """);
    }

    public async Task<T> WithDb<T>(Func<MeridianDbContext, Task<T>> f)
    {
        using var scope = Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<MeridianDbContext>());
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;

/// <summary>Stands in for Azure Document Intelligence in tests.</summary>
public sealed class FakeLayoutReader : ILayoutReader
{
    public Func<LayoutDocument>? Next { get; set; }
    public int Calls;
    public bool IsConfigured => true;

    public Task<LayoutDocument> ReadAsync(Stream pdf, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult((Next ?? throw new InvalidOperationException("No fake layout configured"))());
    }
}

public static class Http
{
    public const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";

    public static string Row(string vote, string code, string ballot, string title = "Maritime Safety", string date = "2024-01-11", string published = "2024-04-02T09:00:00Z") =>
        $"{vote},79,{date},\"{title}\",{code},{code},{ballot},{published}";

    public static async Task<HttpResponseMessage> PostFile(this HttpClient c, string url, string fileName, byte[] content, string type)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "File", fileName);
        return await c.PostAsync(url, form);
    }

    public static Task<HttpResponseMessage> UploadCsv(this HttpClient c, string fileName, IEnumerable<string> rows) =>
        c.PostFile("/api/imports", fileName, Encoding.UTF8.GetBytes(Header + "\n" + string.Join("\n", rows) + "\n"), "text/csv");

    public static async Task<T> Read<T>(this HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;

    public static async Task<T> GetJson<T>(this HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"{url} → {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Read<T>();
    }

    public static Task<HttpResponseMessage> PostJson(this HttpClient c, string url, object body) => c.PostAsJsonAsync(url, body, ApiFactory.Json);
    public static Task<HttpResponseMessage> PutJson(this HttpClient c, string url, object body) => c.PutAsJsonAsync(url, body, ApiFactory.Json);

    /// <summary>Upload and wait until the background import has finished.</summary>
    public static async Task<ImportDto> ImportAndWait(this HttpClient c, string fileName, IEnumerable<string> rows)
    {
        var r = await c.UploadCsv(fileName, rows);
        r.EnsureSuccessStatusCode();
        return await c.WaitImport((await r.Read<ImportDto>()).Id);
    }

    public static async Task<ImportDto> WaitImport(this HttpClient c, long id, int seconds = 120)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var dto = await c.GetJson<ImportDto>($"/api/imports/{id}");
            if (dto.Status is ImportStatus.Completed or ImportStatus.CompletedWithIssues or ImportStatus.Failed) return dto;
            if (DateTime.UtcNow > until) throw new TimeoutException($"Import {id} still {dto.Status}");
            await Task.Delay(100);
        }
    }

    public static async Task<SnapshotRankingDto> WaitSnapshot(this HttpClient c, long id, int seconds = 120)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var dto = await c.GetJson<SnapshotRankingDto>($"/api/snapshots/{id}");
            if (dto.Snapshot.Status is JobStatus.Completed or JobStatus.Failed) return dto;
            if (DateTime.UtcNow > until) throw new TimeoutException($"Snapshot {id} still {dto.Snapshot.Status}");
            await Task.Delay(100);
        }
    }

    public static async Task<ExtractionDto> WaitExtraction(this HttpClient c, long id, Func<ExtractionDto, bool> done, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var dto = await c.GetJson<ExtractionDto>($"/api/extractions/{id}");
            if (done(dto)) return dto;
            if (DateTime.UtcNow > until) throw new TimeoutException($"Extraction {id} still {dto.Status}");
            await Task.Delay(100);
        }
    }

    public static string Q(string voteId) => Uri.EscapeDataString(voteId);
}
