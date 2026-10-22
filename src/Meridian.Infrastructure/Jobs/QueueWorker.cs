using System.Collections.Concurrent;
using Meridian.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meridian.Infrastructure.Jobs;

/// <summary>Wakes a worker right after a job is queued, so it doesn't wait for the next poll.</summary>
public sealed class JobSignal
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new();

    private SemaphoreSlim For(string queue) => _signals.GetOrAdd(queue, _ => new SemaphoreSlim(0));

    public void Notify(string queue)
    {
        var s = For(queue);
        if (s.CurrentCount == 0) s.Release();
    }

    public Task WaitAsync(string queue, TimeSpan timeout, CancellationToken ct) => For(queue).WaitAsync(timeout, ct);
}

public static class Queues
{
    public const string Imports = "imports";
    public const string Snapshots = "snapshots";
    public const string Extractions = "extractions";
}

/// <summary>
/// The job queue is a database table (status column), claimed with FOR UPDATE SKIP LOCKED.
/// Jobs survive restarts and progress is read from the same row (ING-02).
/// </summary>
public abstract class QueueWorker(IServiceScopeFactory scopes, JobSignal signal, ILogger log) : BackgroundService
{
    protected abstract string Queue { get; }
    /// <summary>SQL that atomically claims one job and returns its id as "Value".</summary>
    protected abstract string ClaimSql { get; }
    protected virtual string? RecoverSql => null;
    protected abstract Task ProcessAsync(IServiceProvider services, long id, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long? id;
                using (var scope = scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
                    id = (await db.Database.SqlQueryRaw<long>(ClaimSql).ToListAsync(stoppingToken)).Cast<long?>().FirstOrDefault();
                }

                if (id is null)
                {
                    await signal.WaitAsync(Queue, TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                using (var scope = scopes.CreateScope())
                {
                    await ProcessAsync(scope.ServiceProvider, id.Value, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "{Queue} worker loop error", Queue);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        if (RecoverSql is null) return;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
                var n = await db.Database.ExecuteSqlRawAsync(RecoverSql, ct);
                if (n > 0) log.LogWarning("{Queue}: recovered {Count} interrupted job(s)", Queue, n);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "{Queue}: recovery attempt {Attempt} failed (database not ready?)", Queue, attempt);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
}
