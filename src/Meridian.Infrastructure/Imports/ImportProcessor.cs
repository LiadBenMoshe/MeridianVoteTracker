using System.Text.Json;
using Meridian.Core.Domain;
using Meridian.Core.Extraction;
using Meridian.Core.Ingestion;
using Meridian.Infrastructure.Data;
using Meridian.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Imports;

/// <summary>
/// Runs one queued import end to end (ING-01..15):
/// read → validate/group/in-file checks (FileAnalyzer) → per chunk of votes: compare with approved data
/// (VoteReconciler) and write new data, conflicts, events and the per-row report in one transaction.
/// Approved values are never updated here (ING-08); only inserts happen.
/// </summary>
public sealed class ImportProcessor(MeridianDbContext db, IFileStore files, ILogger<ImportProcessor> log)
{
    private const int VotesPerChunk = 250;

    public async Task ProcessAsync(long importId, CancellationToken ct)
    {
        var import = await db.Imports.SingleAsync(i => i.Id == importId, ct);
        using var scope = log.BeginScope("CorrelationId:{CorrelationId} ImportId:{ImportId}", import.CorrelationId, importId);
        Correlation.Id = import.CorrelationId;
        log.LogInformation("Import {ImportId} started: {File}", importId, import.FileName);

        try
        {
            await RunAsync(import, ct);
            log.LogInformation("Import {ImportId} finished: {Status} new={New} unchanged={Unchanged} conflicts={Conflicts}",
                importId, import.Status, import.NewRows, import.UnchangedRows, import.ConflictsCreated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Import {ImportId} failed", importId);
            db.ChangeTracker.Clear();
            await db.Imports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, ImportStatus.Failed)
                .SetProperty(i => i.Error, ex is CsvHeaderException ? ex.Message : $"Processing failed: {ex.Message}. Data written before the failure is consistent; re-uploading the file is safe.")
                .SetProperty(i => i.FinishedAt, DateTimeOffset.UtcNow), CancellationToken.None);
        }
    }

    private async Task RunAsync(Import import, CancellationToken ct)
    {
        await SetPhase(import, "Reading file", ct);
        var members = await MemberRegistryLoader.LoadAsync(db, ct);

        FileAnalysis analysis;
        if (import.Source == ImportSource.PdfExtraction)
        {
            var extraction = await db.Extractions.AsNoTracking().SingleAsync(e => e.Id == import.ExtractionId, ct);
            var sheet = JsonSerializer.Deserialize<ExtractedSheet>(extraction.SheetJson!)!;
            analysis = FileAnalyzer.Analyze(ExtractionValidator.ToRawRows(sheet, extraction.FileName, extraction.CreatedAt), members);
        }
        else
        {
            await using var stream = await files.OpenReadAsync(import.BlobPath!, ct);
            analysis = FileAnalyzer.Analyze(CsvRowReader.Read(stream), members);
        }

        import.TotalRows = analysis.TotalRows;
        import.VotesInFile = analysis.Votes.Count(v => v.Details is not null || v.DetailsContradiction is not null || v.Invalid > 0);
        import.PublishedAt = analysis.PublishedAt;
        import.NormalizationJson = BuildNormalizationJson(analysis);
        import.Phase = "Applying";
        import.HeartbeatAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var noteByRow = analysis.Notes.GroupBy(n => n.RowNo)
            .ToDictionary(g => g.Key, g => "Normalized: " + string.Join("; ", g.Select(n => $"{n.Field} '{n.Original}' → '{n.Normalized}'")));
        var rejectedByVote = analysis.Rejected.GroupBy(r => r.VoteId ?? "")
            .ToDictionary(g => g.Key, g => g.ToList());

        // Rows without a usable vote id belong to no vote: write them first.
        if (rejectedByVote.TryGetValue("", out var orphan))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await CopyImportRowsAsync(import.Id, orphan, noteByRow, ct);
            Count(import, orphan);
            import.ProcessedRows += orphan.Count;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var ordinal = 0;
        foreach (var chunk in analysis.Votes.Chunk(VotesPerChunk))
        {
            ct.ThrowIfCancellationRequested();
            var existing = await LoadExistingAsync(chunk.Select(v => v.VoteId).ToArray(), ct);
            var now = DateTimeOffset.UtcNow;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var newBallots = new List<(string VoteId, NewBallot Ballot)>();
            var rowResults = new List<RowResult>();
            var conflictsByVote = new Dictionary<string, List<Conflict>>();

            foreach (var cand in chunk)
            {
                existing.TryGetValue(cand.VoteId, out var ex);
                var rec = VoteReconciler.Reconcile(cand, ex);

                if (rec.CreateVote is not null)
                {
                    db.Votes.Add(new Vote
                    {
                        Id = cand.VoteId,
                        Session = rec.CreateVote.Session,
                        VoteDate = rec.CreateVote.Date,
                        Title = rec.CreateVote.Title,
                        DetailsSourceImportId = import.Id,
                        CreatedAt = now,
                    });
                    db.VoteEvents.Add(new VoteEvent
                    {
                        VoteId = cand.VoteId, At = now, Type = VoteEventTypes.VoteCreated, ImportId = import.Id,
                        Message = $"{rec.NewBallots.Count} country votes from {import.FileName}",
                    });
                }
                else if (rec.NewBallots.Count > 0)
                {
                    db.VoteEvents.Add(new VoteEvent
                    {
                        VoteId = cand.VoteId, At = now, Type = VoteEventTypes.CountryVotesAdded, ImportId = import.Id,
                        Message = $"{rec.NewBallots.Count} country vote(s) added from {import.FileName}: " +
                                  string.Join(", ", rec.NewBallots.Take(10).Select(b => $"{b.CountryCode}={b.Ballot}")) +
                                  (rec.NewBallots.Count > 10 ? ", …" : ""),
                    });
                }

                newBallots.AddRange(rec.NewBallots.Select(b => (cand.VoteId, b)));

                if (rec.NewConflicts.Count > 0)
                {
                    var list = rec.NewConflicts.Select(c => new Conflict
                    {
                        Kind = c.Kind,
                        VoteId = cand.VoteId,
                        CountryCode = c.CountryCode,
                        Field = c.Field,
                        ApprovedValue = c.ApprovedValue,
                        IncomingValue = c.IncomingValue,
                        IncomingImportId = import.Id,
                        CreatedAt = now,
                        Status = ConflictStatus.Pending,
                    }).ToList();
                    conflictsByVote[cand.VoteId] = list;
                    db.Conflicts.AddRange(list);
                    import.ConflictsCreated += list.Count;
                }

                rowResults.AddRange(rec.Rows);
                if (rejectedByVote.TryGetValue(cand.VoteId, out var rejected)) rowResults.AddRange(rejected);

                db.ImportVotes.Add(new ImportVote
                {
                    ImportId = import.Id, VoteId = cand.VoteId, Ordinal = ordinal++,
                    Title = cand.Details?.Title ?? ex?.Details.Title,
                    Outcome = rec.Outcome, Details = rec.Details,
                });
            }

            // Votes must exist before their ballots / conflicts reference them.
            await db.SaveChangesAsync(ct);

            if (conflictsByVote.Count > 0)
            {
                await FillApprovedSourcesAsync(conflictsByVote.Values.SelectMany(c => c).ToList(), ct);
                foreach (var (voteId, list) in conflictsByVote)
                {
                    db.VoteEvents.Add(new VoteEvent
                    {
                        VoteId = voteId, At = now, Type = VoteEventTypes.ConflictsCreated, ImportId = import.Id,
                        Message = $"{list.Count} conflict(s) created from {import.FileName}: " + string.Join(", ", list.Select(DescribeConflict)),
                    });
                }
            }

            await CopyBallotsAsync(import.Id, newBallots, now, ct);
            await CopyImportRowsAsync(import.Id, rowResults, noteByRow, ct);
            Count(import, rowResults);
            import.ProcessedRows += chunk.Sum(v => v.TotalRows);
            import.HeartbeatAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            db.Attach(import);
        }

        import.ProcessedRows = import.TotalRows ?? import.ProcessedRows;
        import.Phase = null;
        import.FinishedAt = DateTimeOffset.UtcNow;
        import.Status = import.InvalidRows + import.ContradictionRows + import.ConflictRows > 0
            ? ImportStatus.CompletedWithIssues
            : ImportStatus.Completed;
        await db.SaveChangesAsync(ct);
    }

    private static string DescribeConflict(Conflict c) => c.Kind == ConflictKind.CountryVote
        ? $"{c.CountryCode} {Ballots.Describe(c.ApprovedValue[0])} → {Ballots.Describe(c.IncomingValue[0])}"
        : $"{VoteReconciler.FieldLabel(c.Field)} \"{c.ApprovedValue}\" → \"{c.IncomingValue}\"";

    private static void Count(Import import, IEnumerable<RowResult> rows)
    {
        foreach (var r in rows)
        {
            switch (r.Outcome)
            {
                case RowOutcome.New: import.NewRows++; break;
                case RowOutcome.Unchanged: import.UnchangedRows++; break;
                case RowOutcome.Duplicate: import.DuplicateRows++; break;
                case RowOutcome.Invalid: import.InvalidRows++; break;
                case RowOutcome.Contradiction: import.ContradictionRows++; break;
                case RowOutcome.Conflict: import.ConflictRows++; break;
            }
        }
    }

    private async Task SetPhase(Import import, string phase, CancellationToken ct)
    {
        import.Phase = phase;
        import.HeartbeatAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<string, ExistingVote>> LoadExistingAsync(string[] voteIds, CancellationToken ct)
    {
        var votes = await db.Votes.AsNoTracking().Where(v => voteIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Session, v.VoteDate, v.Title }).ToListAsync(ct);
        if (votes.Count == 0) return new();

        var ids = votes.Select(v => v.Id).ToArray();
        var ballots = await db.CountryVotes.AsNoTracking().Where(c => ids.Contains(c.VoteId))
            .Select(c => new { c.VoteId, c.CountryCode, c.Ballot }).ToListAsync(ct);
        var conflicts = await db.Conflicts.AsNoTracking().Where(c => ids.Contains(c.VoteId))
            .Select(c => new { c.Id, c.VoteId, c.CountryCode, c.Field, c.ApprovedValue, c.IncomingValue, c.Status }).ToListAsync(ct);

        var ballotLookup = ballots.ToLookup(b => b.VoteId);
        var conflictLookup = conflicts.ToLookup(c => c.VoteId);
        return votes.ToDictionary(v => v.Id, v => new ExistingVote
        {
            Details = new VoteDetails(v.Session, v.VoteDate, v.Title),
            Ballots = ballotLookup[v.Id].ToDictionary(b => b.CountryCode, b => b.Ballot, StringComparer.Ordinal),
            Conflicts = conflictLookup[v.Id].Select(c => new KnownConflict(c.Id, c.CountryCode, c.Field, c.ApprovedValue, c.IncomingValue, c.Status)).ToList(),
        });
    }

    /// <summary>CON-02: remember where the approved value came from.</summary>
    private async Task FillApprovedSourcesAsync(List<Conflict> conflicts, CancellationToken ct)
    {
        var voteIds = conflicts.Select(c => c.VoteId).Distinct().ToArray();
        var voteSources = await db.Votes.AsNoTracking().Where(v => voteIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.DetailsSourceImportId, ct);
        var cells = await db.CountryVotes.AsNoTracking().Where(c => voteIds.Contains(c.VoteId))
            .Select(c => new { c.VoteId, c.CountryCode, c.SourceImportId }).ToListAsync(ct);
        var cellSources = cells.ToDictionary(c => (c.VoteId, c.CountryCode), c => c.SourceImportId);
        foreach (var c in conflicts)
        {
            c.ApprovedSourceImportId = c.Kind == ConflictKind.CountryVote
                ? cellSources.GetValueOrDefault((c.VoteId, c.CountryCode))
                : voteSources.GetValueOrDefault(c.VoteId);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task CopyBallotsAsync(long importId, List<(string VoteId, NewBallot Ballot)> ballots, DateTimeOffset now, CancellationToken ct)
    {
        if (ballots.Count == 0) return;
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY country_votes (vote_id, country_code, ballot, source_import_id, approved_at, approved_by) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var (voteId, b) in ballots)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(voteId, NpgsqlDbType.Varchar, ct);
            await w.WriteAsync(b.CountryCode, NpgsqlDbType.Char, ct);
            await w.WriteAsync(b.Ballot.ToString(), NpgsqlDbType.Char, ct);
            await w.WriteAsync(importId, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            await w.WriteAsync("system", NpgsqlDbType.Text, ct);
        }
        await w.CompleteAsync(ct);
    }

    private async Task CopyImportRowsAsync(long importId, IEnumerable<RowResult> rows, IReadOnlyDictionary<int, string> notes, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY import_rows (import_id, row_no, vote_id, country_code, ballot, outcome, reason, raw) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var r in rows)
        {
            var reason = r.Reason;
            if (notes.TryGetValue(r.RowNo, out var note)) reason = reason is null ? note : $"{reason}. {note}";
            await w.StartRowAsync(ct);
            await w.WriteAsync(importId, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(r.RowNo, NpgsqlDbType.Integer, ct);
            await WriteNullable(w, Truncate(r.VoteId, 64), ct);
            await WriteNullable(w, Truncate(r.CountryCode, 64), ct);
            await WriteNullable(w, Truncate(r.Ballot, 32), ct);
            await w.WriteAsync(r.Outcome.ToString(), NpgsqlDbType.Varchar, ct);
            await WriteNullable(w, reason, ct);
            await WriteNullable(w, r.Raw, ct);
        }
        await w.CompleteAsync(ct);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static async Task WriteNullable(NpgsqlBinaryImporter w, string? value, CancellationToken ct)
    {
        if (value is null) await w.WriteNullAsync(ct);
        else await w.WriteAsync(value, NpgsqlDbType.Text, ct);
    }

    private static string BuildNormalizationJson(FileAnalysis analysis)
    {
        var applied = analysis.Notes.GroupBy(n => n.Rule).Select(g => new
        {
            rule = g.Key,
            count = g.Count(),
            examples = g.Take(20).Select(n => new { row = n.RowNo, field = n.Field, original = n.Original, normalized = n.Normalized }),
        });
        return JsonSerializer.Serialize(new { rules = RowValidator.NormalizationRules, applied });
    }
}
