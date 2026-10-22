using System.Text.Json;
using Meridian.Api.Contracts;
using Meridian.Api.Infrastructure;
using Meridian.Core.Domain;
using Meridian.Core.Extraction;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Meridian.Infrastructure.Extraction;
using Meridian.Infrastructure.Jobs;
using Meridian.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Controllers;

/// <summary>Bonus: scanned voting sheets → Azure Document Intelligence → human review → normal import rules.</summary>
[ApiController]
[Route("api/extractions")]
[Authorize(Policy = Policies.Analyst)]
public sealed class ExtractionsController(MeridianDbContext db, IFileStore files, JobSignal signal, ILayoutReader reader) : ControllerBase
{
    /// <summary>Upload a scanned PDF. Extraction runs in the background (PDF-01).</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    [ProducesResponseType<ExtractionDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ExtractionDto>> Upload([FromForm] UploadFileRequest request, CancellationToken ct)
    {
        var file = request.File!;
        if (file.Length == 0) throw FieldValidationException.For("File", "The file is empty.");
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw FieldValidationException.For("File", "Only .pdf files can be extracted.");

        var name = Path.GetFileName(file.FileName);
        var path = $"extractions/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}-{name}";
        await using (var s = file.OpenReadStream()) await files.SaveAsync(path, s, "application/pdf", ct);

        var e = new Extraction
        {
            FileName = name, BlobPath = path, UploadedBy = User.UserId(), CreatedAt = DateTimeOffset.UtcNow,
            Status = ExtractionStatus.Queued, CorrelationId = Correlation.Id,
        };
        db.Extractions.Add(e);
        await db.SaveChangesAsync(ct);
        signal.Notify(Queues.Extractions);
        return AcceptedAtAction(nameof(Get), new { id = e.Id }, ToDto(e));
    }

    [HttpGet]
    public async Task<ActionResult<List<ExtractionDto>>> List([FromQuery] ExtractionStatus? status, CancellationToken ct)
    {
        var q = db.Extractions.AsNoTracking();
        if (status is not null) q = q.Where(e => e.Status == status);
        var list = await q.OrderByDescending(e => e.Id).Take(200).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ExtractionDto>> Get(long id, CancellationToken ct) => ToDto(await Load(id, ct));

    [HttpGet("{id:long}/file")]
    [Produces("application/pdf")]
    public async Task<IActionResult> Download(long id, CancellationToken ct)
    {
        var e = await Load(id, ct);
        return File(await files.OpenReadAsync(e.BlobPath, ct), "application/pdf", e.FileName);
    }

    /// <summary>Save the analyst's edits. Values are re-checked against the domain rules (PDF-03/04).</summary>
    [HttpPut("{id:long}")]
    public async Task<ActionResult<ExtractionDto>> Update(long id, [FromBody] UpdateExtractionRequest request, CancellationToken ct)
    {
        var e = await LoadForReview(id, request.Version, ct);
        var sheet = request.Sheet!;
        ExtractionValidator.Validate(sheet, await MemberRegistryLoader.LoadAsync(db, ct));
        e.SheetJson = JsonSerializer.Serialize(sheet);
        e.IssueCount = sheet.Issues.Count + sheet.Rows.Count(r => r.Issues.Count > 0);
        await db.SaveChangesAsync(ct);
        return ToDto(e);
    }

    /// <summary>
    /// Approve: becomes a normal import, classified exactly like CSV rows: new, unchanged or conflict (PDF-05).
    /// Blocked while issues remain.
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public async Task<ActionResult<ExtractionDto>> Approve(long id, [FromBody] ReviewExtractionRequest request, CancellationToken ct)
    {
        var e = await LoadForReview(id, request.Version, ct);
        var sheet = JsonSerializer.Deserialize<ExtractedSheet>(e.SheetJson!)!;
        ExtractionValidator.Validate(sheet, await MemberRegistryLoader.LoadAsync(db, ct));
        if (ExtractionValidator.HasIssues(sheet))
            throw new ApiException(422, "Issues not resolved", "Fix or confirm the flagged rows before approving.",
                new Dictionary<string, object?> { ["issues"] = sheet.Issues });

        var import = new Import
        {
            FileName = e.FileName, Source = ImportSource.PdfExtraction, ExtractionId = e.Id,
            UploadedBy = User.UserId(), UploadedAt = DateTimeOffset.UtcNow, Status = ImportStatus.Queued,
            Phase = "Waiting in queue", CorrelationId = Correlation.Id,
        };
        db.Imports.Add(import);
        e.Status = ExtractionStatus.Approved;
        e.ReviewedBy = User.UserId();
        e.ReviewedAt = DateTimeOffset.UtcNow;
        e.ReviewComment = request.Comment;
        await db.SaveChangesAsync(ct);
        e.ImportId = import.Id;
        await db.SaveChangesAsync(ct);
        signal.Notify(Queues.Imports);
        return ToDto(e);
    }

    [HttpPost("{id:long}/reject")]
    public async Task<ActionResult<ExtractionDto>> Reject(long id, [FromBody] ReviewExtractionRequest request, CancellationToken ct)
    {
        var e = await LoadForReview(id, request.Version, ct);
        e.Status = ExtractionStatus.Rejected;
        e.ReviewedBy = User.UserId();
        e.ReviewedAt = DateTimeOffset.UtcNow;
        e.ReviewComment = request.Comment;
        await db.SaveChangesAsync(ct);
        return ToDto(e);
    }

    /// <summary>Retry a failed extraction (PDF-06).</summary>
    [HttpPost("{id:long}/retry")]
    public async Task<ActionResult<ExtractionDto>> Retry(long id, CancellationToken ct)
    {
        var e = await Load(id, ct);
        if (e.Status != ExtractionStatus.Failed) throw new ApiException(409, "Cannot retry", "Only failed extractions can be retried.");
        e.Status = ExtractionStatus.Queued;
        e.Attempts = 0;
        e.NextAttemptAt = null;
        e.Error = null;
        e.CorrelationId = Correlation.Id;
        await db.SaveChangesAsync(ct);
        signal.Notify(Queues.Extractions);
        return ToDto(e);
    }

    private async Task<Extraction> Load(long id, CancellationToken ct) =>
        await db.Extractions.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw ApiException.NotFound($"Extraction {id}");

    private async Task<Extraction> LoadForReview(long id, uint version, CancellationToken ct)
    {
        var e = await Load(id, ct);
        if (e.Status != ExtractionStatus.NeedsReview)
            throw new ApiException(409, "Already reviewed", $"This extraction is {e.Status} (by {e.ReviewedBy ?? "system"}).");
        // Optimistic concurrency on xmin: a stale version → DbUpdateConcurrencyException → 409.
        db.Entry(e).Property(x => x.Version).OriginalValue = version;
        return e;
    }

    private ExtractionDto ToDto(Extraction e) => ToDto(e, includeSheet: true);

    private ExtractionDto ToDto(Extraction e, bool includeSheet) => new(
        e.Id, e.FileName, e.UploadedBy, e.CreatedAt, e.Status, e.Attempts, e.NextAttemptAt, e.Error, e.IssueCount,
        includeSheet && e.SheetJson is not null ? JsonSerializer.Deserialize<ExtractedSheet>(e.SheetJson) : null,
        e.ReviewedBy, e.ReviewedAt, e.ReviewComment, e.ImportId, e.Version, reader.IsConfigured);
}
