namespace Meridian.Core.Domain;

/// <summary>Exactly one classification per imported row (ING-06).</summary>
public enum RowOutcome
{
    New = 0,
    Unchanged = 1,
    Duplicate = 2,
    Invalid = 3,
    Contradiction = 4,
    Conflict = 5,
}

public enum ImportStatus
{
    Queued = 0,
    Processing = 1,
    Completed = 2,
    CompletedWithIssues = 3,
    Failed = 4,
}

public enum ImportSource
{
    Csv = 0,
    PdfExtraction = 1,
    Seed = 2,
}

public enum ConflictKind
{
    CountryVote = 0,
    VoteDetail = 1,
}

public enum ConflictStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

public enum ExtractionStatus
{
    Queued = 0,
    Running = 1,
    NeedsReview = 2,
    Approved = 3,
    Rejected = 4,
    Failed = 5,
}

public static class Roles
{
    public const string Analyst = "Analyst";
    public const string Admin = "Admin";
}

public static class Ballots
{
    public const char Yes = 'Y';
    public const char No = 'N';
    public const char Abstain = 'A';
    public const char Absent = 'X';

    public static bool IsValid(char c) => c is Yes or No or Abstain or Absent;

    public static string Describe(char c) => c switch
    {
        Yes => "YES",
        No => "NO",
        Abstain => "ABSTAIN",
        Absent => "ABSENT",
        _ => c.ToString(),
    };
}

/// <summary>Vote-level fields that can be in conflict (CON-03).</summary>
public static class VoteFields
{
    public const string Ballot = "ballot";
    public const string Session = "session";
    public const string Date = "vote_date";
    public const string Title = "resolution_title";
}
