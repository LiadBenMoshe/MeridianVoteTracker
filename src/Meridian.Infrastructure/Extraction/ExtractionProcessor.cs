using System.Text.Json;
using Azure;
using Meridian.Core.Domain;
using Meridian.Core.Extraction;
using Meridian.Infrastructure.Data;
using Meridian.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meridian.Infrastructure.Extraction;

/// <summary>
/// Runs the AI extraction for one PDF (PDF-01..03, PDF-06). The result only goes to review;
/// nothing becomes approved data here (PDF-05).
/// </summary>
public sealed class ExtractionProcessor(MeridianDbContext db, IFileStore files, ILayoutReader reader, IOptions<DocumentIntelligenceOptions> options, ILogger<ExtractionProcessor> log)
{
    public const int MaxAttempts = 3;

    public async Task ProcessAsync(long id, CancellationToken ct)
    {
        var e = await db.Extractions.SingleAsync(x => x.Id == id, ct);
        Correlation.Id = e.CorrelationId;
        using var scope = log.BeginScope("CorrelationId:{CorrelationId} ExtractionId:{ExtractionId}", e.CorrelationId, id);
        try
        {
            LayoutDocument layout;
            await using (var pdf = await files.OpenReadAsync(e.BlobPath, ct))
                layout = await reader.ReadAsync(pdf, ct);

            var sheet = SheetParser.Parse(layout);
            ExtractionValidator.Validate(sheet, await MemberRegistryLoader.LoadAsync(db, ct));

            e.SheetJson = JsonSerializer.Serialize(sheet);
            e.IssueCount = sheet.Issues.Count + sheet.Rows.Count(r => r.Issues.Count > 0);
            e.Status = ExtractionStatus.NeedsReview;
            e.Error = null;
            e.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Extraction {Id} ready for review: {Rows} rows, {Issues} issue(s)", id, sheet.Rows.Count, e.IssueCount);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // PDF-06: failures are visible, temporary ones are retried with backoff, and nothing stays "Running".
            var (permanent, message) = ex switch
            {
                InvalidOperationException => (true, ex.Message),
                // 4xx (except timeout / throttling): the document itself was rejected, retrying will not help.
                RequestFailedException r when r.Status is >= 400 and < 500 and not 408 and not 429 =>
                    (true, $"Azure Document Intelligence rejected the file ({r.Status} {r.ErrorCode}). Check that it is a readable PDF."),
                RequestFailedException r => (false, $"Azure Document Intelligence error ({r.Status} {r.ErrorCode})."),
                _ => (false, ex.Message.Split('\n')[0]),
            };
            permanent |= e.Attempts >= MaxAttempts;
            log.LogError(ex, "Extraction {Id} attempt {Attempt} failed (permanent={Permanent})", id, e.Attempts, permanent);
            e.Error = permanent ? message : $"Attempt {e.Attempts} of {MaxAttempts} failed: {message} Retrying automatically.";
            if (permanent)
            {
                e.Status = ExtractionStatus.Failed;
                e.FinishedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                e.Status = ExtractionStatus.Queued;
                e.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(options.Value.RetryDelaySeconds * e.Attempts);
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
