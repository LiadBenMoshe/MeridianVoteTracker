using Meridian.Infrastructure.Extraction;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Snapshots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meridian.Infrastructure.Jobs;

/// <summary>Processes imports one at a time, so two files never race on the same vote.</summary>
public sealed class ImportWorker(IServiceScopeFactory scopes, JobSignal signal, ILogger<ImportWorker> log)
    : QueueWorker(scopes, signal, log)
{
    protected override string Queue => Queues.Imports;

    protected override string ClaimSql => """
        UPDATE imports SET status = 'Processing', started_at = now(), heartbeat_at = now()
        WHERE id = (SELECT id FROM imports WHERE status = 'Queued' ORDER BY id FOR UPDATE SKIP LOCKED LIMIT 1)
        RETURNING id AS "Value"
        """;

    // An import interrupted by a restart is marked failed, not silently re-run: its report would be wrong.
    protected override string RecoverSql => """
        UPDATE imports SET status = 'Failed', finished_at = now(),
               error = 'Interrupted (service restarted). Data written so far is consistent; re-uploading the file is safe.'
        WHERE status = 'Processing' AND (heartbeat_at IS NULL OR heartbeat_at < now() - interval '2 minutes')
        """;

    protected override Task ProcessAsync(IServiceProvider services, long id, CancellationToken ct)
    {
        Correlation.Id = null;
        return services.GetRequiredService<ImportProcessor>().ProcessAsync(id, ct);
    }
}

public sealed class SnapshotWorker(IServiceScopeFactory scopes, JobSignal signal, ILogger<SnapshotWorker> log)
    : QueueWorker(scopes, signal, log)
{
    protected override string Queue => Queues.Snapshots;

    protected override string ClaimSql => """
        UPDATE snapshots SET status = 'Running', phase = 'Starting'
        WHERE id = (SELECT id FROM snapshots WHERE status = 'Queued' ORDER BY id FOR UPDATE SKIP LOCKED LIMIT 1)
        RETURNING id AS "Value"
        """;

    // Snapshot calculation is a pure function of frozen inputs, so an interrupted one is simply re-queued.
    protected override string RecoverSql => "UPDATE snapshots SET status = 'Queued', progress = 0 WHERE status = 'Running'";

    protected override Task ProcessAsync(IServiceProvider services, long id, CancellationToken ct) =>
        services.GetRequiredService<SnapshotCalculator>().CalculateAsync(id, ct);
}

public sealed class ExtractionWorker(IServiceScopeFactory scopes, JobSignal signal, ILogger<ExtractionWorker> log)
    : QueueWorker(scopes, signal, log)
{
    protected override string Queue => Queues.Extractions;

    protected override string ClaimSql => """
        UPDATE extractions SET status = 'Running', started_at = now(), attempts = attempts + 1
        WHERE id = (SELECT id FROM extractions
                    WHERE status = 'Queued' AND (next_attempt_at IS NULL OR next_attempt_at <= now())
                    ORDER BY id FOR UPDATE SKIP LOCKED LIMIT 1)
        RETURNING id AS "Value"
        """;

    // PDF-06: nothing stays "Running" forever.
    protected override string RecoverSql => """
        UPDATE extractions SET status = 'Queued', next_attempt_at = now()
        WHERE status = 'Running' AND attempts < 3;
        UPDATE extractions SET status = 'Failed', error = 'Interrupted too many times', finished_at = now()
        WHERE status = 'Running';
        """;

    protected override Task ProcessAsync(IServiceProvider services, long id, CancellationToken ct) =>
        services.GetRequiredService<ExtractionProcessor>().ProcessAsync(id, ct);
}
