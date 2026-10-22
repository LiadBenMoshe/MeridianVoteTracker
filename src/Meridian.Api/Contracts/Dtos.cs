using System.ComponentModel.DataAnnotations;
using Meridian.Core.Domain;
using Meridian.Core.Extraction;

namespace Meridian.Api.Contracts;

public sealed record UserDto(string Id, string Name, string Role);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

// ---------- Imports ----------

public sealed record ImportCountsDto(int New, int Unchanged, int Duplicate, int Invalid, int Contradiction, int Conflict, int ConflictsCreated);

public sealed record ImportDto(
    long Id,
    string FileName,
    ImportSource Source,
    long? ExtractionId,
    string UploadedBy,
    DateTimeOffset UploadedAt,
    ImportStatus Status,
    string? Phase,
    int? TotalRows,
    int ProcessedRows,
    int Percent,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? PublishedAt,
    string? Error,
    int VotesInFile,
    ImportCountsDto Counts,
    long? SameContentAsImportId);

public sealed record ImportVoteDto(string VoteId, string? Title, string Outcome, string Details);

public sealed record ImportRowDto(int RowNo, string? VoteId, string? CountryCode, string? Ballot, RowOutcome Outcome, string? Reason, string? Raw);

public sealed record NormalizationExampleDto(int Row, string Field, string Original, string Normalized);

public sealed record NormalizationAppliedDto(string Rule, int Count, IReadOnlyList<NormalizationExampleDto> Examples);

public sealed record NormalizationDto(IReadOnlyList<string> Rules, IReadOnlyList<NormalizationAppliedDto> Applied);

public sealed record ImportReportDto(
    ImportDto Import,
    IReadOnlyList<ImportVoteDto> Votes,
    IReadOnlyList<ImportRowDto> ProblemRows,
    int ProblemRowsTotal,
    NormalizationDto Normalization);

// ---------- Conflicts ----------

public sealed record SourceDto(long? ImportId, string? FileName, string? UploadedBy, DateTimeOffset? UploadedAt, DateTimeOffset? PublishedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt);

public sealed record ConflictDto(
    long Id,
    ConflictKind Kind,
    string VoteId,
    string VoteTitle,
    DateOnly VoteDate,
    int Session,
    string? CountryCode,
    string? CountryName,
    string Field,
    string ApprovedValue,
    string IncomingValue,
    string CurrentApprovedValue,
    SourceDto? ApprovedSource,
    SourceDto IncomingSource,
    ConflictStatus Status,
    DateTimeOffset CreatedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Comment,
    string? ValueBeforeDecision,
    IReadOnlyList<string> Impact);

public enum Decision
{
    Accept,
    Reject,
}

public sealed class DecisionRequest
{
    [Required]
    public Decision? Decision { get; set; }

    [MaxLength(2000)]
    public string? Comment { get; set; }
}

public sealed record ReviewSummaryDto(int PendingConflicts, int ExtractionsToReview, int ExtractionsRunning);

// ---------- Votes ----------

public sealed record VoteListItemDto(string Id, int Session, DateOnly Date, string Title, int CountryVotes, string? Position, int? Weight, int PendingConflicts);

public sealed record VoteListDto(IReadOnlyList<VoteListItemDto> Items, int Total, int Page, int PageSize, int WithoutPositionTotal);

public sealed record TalliesDto(int Yes, int No, int Abstain, int Absent);

public sealed record CountryVoteDto(string Code, string Name, string Ballot, bool ChangedByDecision, string ApprovedBy, DateTimeOffset ApprovedAt, long? SourceImportId, string? SourceFile);

public sealed record VoteDetailDto(
    string Id,
    int Session,
    DateOnly Date,
    string Title,
    string? Position,
    int? Weight,
    string? PositionUpdatedBy,
    DateTimeOffset? PositionUpdatedAt,
    TalliesDto Tallies,
    int PendingConflicts,
    SourceDto? DetailsSource,
    IReadOnlyList<CountryVoteDto> CountryVotes);

public sealed record TimelineEventDto(
    long Id,
    DateTimeOffset At,
    string Actor,
    string Type,
    string? CountryCode,
    string? Field,
    string? OldValue,
    string? NewValue,
    long? ImportId,
    string? ImportFile,
    long? ConflictId,
    long? SnapshotId,
    string? Message);

public sealed class SetPositionRequest
{
    /// <summary>"Y" (institute wants YES) or "N" (institute wants NO).</summary>
    [Required(ErrorMessage = "Choose YES or NO.")]
    [RegularExpression("^[YN]$", ErrorMessage = "Position must be Y (yes) or N (no).")]
    public string? Position { get; set; }

    [Required(ErrorMessage = "Weight is required.")]
    [Range(1, 5, ErrorMessage = "Weight must be between 1 (minor) and 5 (critical).")]
    public int? Weight { get; set; }
}

public sealed class BulkPositionRequest
{
    public int? Session { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public bool OnlyWithoutPosition { get; set; } = true;

    [Required(ErrorMessage = "Choose YES or NO.")]
    [RegularExpression("^[YN]$", ErrorMessage = "Position must be Y (yes) or N (no).")]
    public string? Position { get; set; }

    [Required(ErrorMessage = "Weight is required.")]
    [Range(1, 5, ErrorMessage = "Weight must be between 1 (minor) and 5 (critical).")]
    public int? Weight { get; set; }
}

public sealed record BulkPositionResultDto(int Updated);

// ---------- Snapshots ----------

public sealed class SnapshotSelection
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? Session { get; set; }
    /// <summary>Explicit votes; added to the votes matched by the filter.</summary>
    public List<string>? VoteIds { get; set; }
}

public sealed class CreateSnapshotRequest
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    [Required]
    public SnapshotSelection? Selection { get; set; }
}

public sealed record SnapshotPreviewDto(int VoteCount, int MissingPositionTotal, IReadOnlyList<VoteListItemDto> MissingPosition, IReadOnlyList<string> UnknownVoteIds);

public sealed record SnapshotDto(
    long Id,
    string Name,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    JobStatus Status,
    int Progress,
    string? Phase,
    int VoteCount,
    DateTimeOffset? ComputedAt,
    string? Error,
    string? PublishedQuarter,
    string? PublishedBy,
    DateTimeOffset? PublishedAt,
    string SelectionDescription,
    IReadOnlyList<string> Rules,
    string RulesVersion,
    string? SuggestedQuarter);

public sealed record SnapshotScoreDto(string Code, string Name, double? Score, int? Rank, int MatchedWeight, int CountedWeight, int CountedVotes);

public sealed record SnapshotRankingDto(SnapshotDto Snapshot, int RankedCount, IReadOnlyList<SnapshotScoreDto> Scores);

public sealed record ContributionDto(string VoteId, string Title, DateOnly Date, string Position, int Weight, string? Ballot, bool Counted, bool Matched, string Note, string? CurrentBallot);

public sealed record CountryBreakdownDto(
    SnapshotScoreDto Score,
    int RankedCount,
    IReadOnlyList<string> Rules,
    IReadOnlyList<ContributionDto> Contributions,
    int ChangedSinceSnapshot);

public sealed class PublishRequest
{
    [Required(ErrorMessage = "Quarter is required.")]
    [RegularExpression(@"^\d{4}-Q[1-4]$", ErrorMessage = "Quarter must look like 2024-Q1.")]
    public string? Quarter { get; set; }
}

// ---------- Extractions (bonus) ----------

public sealed record ExtractionDto(
    long Id,
    string FileName,
    string UploadedBy,
    DateTimeOffset CreatedAt,
    ExtractionStatus Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    string? Error,
    int IssueCount,
    ExtractedSheet? Sheet,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewComment,
    long? ImportId,
    uint Version,
    bool AiConfigured);

public sealed class UpdateExtractionRequest
{
    [Required] public ExtractedSheet? Sheet { get; set; }
    public uint Version { get; set; }
}

public sealed class ReviewExtractionRequest
{
    public uint Version { get; set; }
    [MaxLength(2000)] public string? Comment { get; set; }
}
