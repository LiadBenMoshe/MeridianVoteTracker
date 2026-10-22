using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Meridian.Api.Contracts;
using Meridian.Api.Infrastructure;
using Meridian.Core.Domain;
using Meridian.Infrastructure;
using Meridian.Infrastructure.Data;
using Meridian.Infrastructure.Jobs;
using Meridian.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Controllers;

public sealed class UploadFileRequest
{
    [Required]
    public IFormFile? File { get; set; }
}

[ApiController]
[Route("api/imports")]
[Authorize(Policy = Policies.Analyst)]
public sealed class ImportsController(MeridianDbContext db, IFileStore files, JobSignal signal, ILogger<ImportsController> log) : ControllerBase
{
    private const long MaxBytes = 300L * 1024 * 1024;

    /// <summary>Upload an Assembly CSV export. Returns immediately (202); processing runs in the background (ING-01).</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBytes)]
    [ProducesResponseType<ImportDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ImportDto>> Upload([FromForm] UploadFileRequest request, CancellationToken ct)
    {
        var file = request.File!;
        if (file.Length == 0) throw FieldValidationException.For("File", "The file is empty.");
        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw FieldValidationException.For("File", "Only .csv files can be imported here.");

        string sha;
        await using (var s = file.OpenReadStream())
            sha = Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();

        var fileName = Path.GetFileName(file.FileName);
        var blobPath = $"imports/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}-{fileName}";
        await using (var s = file.OpenReadStream())
            await files.SaveAsync(blobPath, s, "text/csv", ct);

        var import = new Import
        {
            FileName = fileName,
            BlobPath = blobPath,
            Sha256 = sha,
            SizeBytes = file.Length,
            Source = ImportSource.Csv,
            UploadedBy = User.UserId(),
            UploadedAt = DateTimeOffset.UtcNow,
            Status = ImportStatus.Queued,
            Phase = "Waiting in queue",
            CorrelationId = Correlation.Id,
        };
        db.Imports.Add(import);
        await db.SaveChangesAsync(ct);
        signal.Notify(Queues.Imports);
        log.LogInformation("Import {ImportId} queued: {File} ({Bytes} bytes)", import.Id, fileName, file.Length);

        return AcceptedAtAction(nameof(Get), new { id = import.Id }, await ToDtoAsync(import, ct));
    }

    /// <summary>History of all imports (HIS-01).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<ImportDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = db.Imports.AsNoTracking().Where(i => i.Source != ImportSource.PdfExtraction || i.Status != ImportStatus.Queued);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(i => i.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<ImportDto>(items.Select(i => ToDto(i, null)).ToList(), total, page, pageSize);
    }

    /// <summary>Status and live progress of one import (ING-02). Poll this; it survives a page refresh.</summary>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<ImportDto>> Get(long id, CancellationToken ct)
    {
        var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) ?? throw ApiException.NotFound($"Import {id}");
        return await ToDtoAsync(import, ct);
    }

    /// <summary>The saved import report (ING-14, ING-15).</summary>
    [HttpGet("{id:long}/report")]
    public async Task<ActionResult<ImportReportDto>> Report(long id, CancellationToken ct)
    {
        var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) ?? throw ApiException.NotFound($"Import {id}");
        var votes = await db.ImportVotes.AsNoTracking().Where(v => v.ImportId == id).OrderBy(v => v.Ordinal)
            .Select(v => new ImportVoteDto(v.VoteId, v.Title, v.Outcome, v.Details)).ToListAsync(ct);

        var problemOutcomes = new[] { RowOutcome.Invalid, RowOutcome.Contradiction, RowOutcome.Duplicate, RowOutcome.Conflict };
        var problems = db.ImportRows.AsNoTracking().Where(r => r.ImportId == id && problemOutcomes.Contains(r.Outcome));
        var problemTotal = await problems.CountAsync(ct);
        var problemRows = await problems.OrderBy(r => r.RowNo).Take(1000)
            .Select(r => new ImportRowDto(r.RowNo, r.VoteId, r.CountryCode, r.Ballot, r.Outcome, r.Reason, r.Raw)).ToListAsync(ct);

        return new ImportReportDto(await ToDtoAsync(import, ct), votes, problemRows, problemTotal, ParseNormalization(import.NormalizationJson));
    }

    /// <summary>What happened to every row of the file (HIS-01). Filter by outcome / vote.</summary>
    [HttpGet("{id:long}/rows")]
    public async Task<ActionResult<PagedResult<ImportRowDto>>> Rows(long id, [FromQuery] RowOutcome? outcome, [FromQuery] string? voteId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var q = db.ImportRows.AsNoTracking().Where(r => r.ImportId == id);
        if (outcome is not null) q = q.Where(r => r.Outcome == outcome);
        if (!string.IsNullOrWhiteSpace(voteId)) q = q.Where(r => r.VoteId == voteId);
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(r => r.RowNo).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new ImportRowDto(r.RowNo, r.VoteId, r.CountryCode, r.Ballot, r.Outcome, r.Reason, r.Raw)).ToListAsync(ct);
        return new PagedResult<ImportRowDto>(items, total, page, pageSize);
    }

    /// <summary>Download the original uploaded file.</summary>
    [HttpGet("{id:long}/file")]
    [Produces("text/csv")]
    public async Task<IActionResult> Download(long id, CancellationToken ct)
    {
        var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) ?? throw ApiException.NotFound($"Import {id}");
        if (import.BlobPath is null) throw new ApiException(404, "No file", "This import has no stored file.");
        return File(await files.OpenReadAsync(import.BlobPath, ct), "text/csv", import.FileName);
    }

    private async Task<ImportDto> ToDtoAsync(Import i, CancellationToken ct)
    {
        long? same = null;
        if (i.Sha256 is not null)
            same = await db.Imports.AsNoTracking().Where(o => o.Sha256 == i.Sha256 && o.Id < i.Id).OrderBy(o => o.Id)
                .Select(o => (long?)o.Id).FirstOrDefaultAsync(ct);
        return ToDto(i, same);
    }

    public static ImportDto ToDto(Import i, long? sameContentAs)
    {
        var done = i.Status is ImportStatus.Completed or ImportStatus.CompletedWithIssues;
        var percent = done ? 100 : i.TotalRows is > 0 ? (int)Math.Min(99, 100L * i.ProcessedRows / i.TotalRows.Value) : 0;
        return new ImportDto(i.Id, i.FileName, i.Source, i.ExtractionId, i.UploadedBy, i.UploadedAt, i.Status, i.Phase, i.TotalRows,
            i.ProcessedRows, percent, i.StartedAt, i.FinishedAt, i.PublishedAt, i.Error, i.VotesInFile,
            new ImportCountsDto(i.NewRows, i.UnchangedRows, i.DuplicateRows, i.InvalidRows, i.ContradictionRows, i.ConflictRows, i.ConflictsCreated),
            sameContentAs);
    }

    private static NormalizationDto ParseNormalization(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new NormalizationDto(Meridian.Core.Ingestion.RowValidator.NormalizationRules, []);
        var doc = JsonDocument.Parse(json).RootElement;
        var rules = doc.GetProperty("rules").EnumerateArray().Select(r => r.GetString()!).ToList();
        var applied = doc.GetProperty("applied").EnumerateArray().Select(a => new NormalizationAppliedDto(
            a.GetProperty("rule").GetString()!,
            a.GetProperty("count").GetInt32(),
            a.GetProperty("examples").EnumerateArray().Select(e => new NormalizationExampleDto(
                e.GetProperty("row").GetInt32(), e.GetProperty("field").GetString()!,
                e.GetProperty("original").GetString()!, e.GetProperty("normalized").GetString()!)).ToList())).ToList();
        return new NormalizationDto(rules, applied);
    }
}
