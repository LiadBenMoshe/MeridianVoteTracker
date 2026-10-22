using System.Text;
using Meridian.Core.Domain;
using Meridian.Core.Ingestion;

namespace Meridian.UnitTests;

internal static class TestData
{
    public const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";

    public static MemberRegistry Members() => new(
    [
        ("BRA", "Brazil", new DateOnly(1995, 1, 1), null),
        ("IND", "India", new DateOnly(1995, 1, 1), null),
        ("JPN", "Japan", new DateOnly(1995, 1, 1), null),
        ("FRA", "France", new DateOnly(1995, 1, 1), null),
        ("DEU", "Germany", new DateOnly(1995, 1, 1), null),
        ("TUR", "Türkiye", new DateOnly(1995, 1, 1), null),
        ("NZL", "New Zealand", new DateOnly(1995, 1, 1), new DateOnly(2021, 6, 30)),
        ("ISL", "Iceland", new DateOnly(1995, 1, 1), new DateOnly(2019, 12, 31)),
        ("ISL", "Iceland", new DateOnly(2022, 1, 1), null),
    ]);

    public static string Row(string vote, string code, string ballot, string title = "Maritime Safety", string date = "2024-01-11",
        string session = "79", string name = "", string published = "2024-04-02T09:00:00Z") =>
        $"{vote},{session},{date},\"{title}\",{code},{(name == "" ? code : name)},{ballot},{published}";

    public static IEnumerable<RawRow> Read(params string[] rows) =>
        CsvRowReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(Header + "\n" + string.Join("\n", rows) + "\n"))).ToList();

    /// <summary>The original file of section 7.2 (vote ids 101..104 become MA/2024/101..104).</summary>
    public static string[] OriginalFile() =>
    [
        Row("MA/2024/101", "BRA", "Y"),                                       // row 2: new
        Row("MA/2024/101", "IND", "Y"),                                       // row 3: new
        Row("MA/2024/102", "JPN", "Y", "Food Security"),                      // row 4: new
        Row("MA/2024/102", "JPN", "Y", "Food Security"),                      // row 5: duplicate of 4
        Row("MA/2024/103", "FRA", "Y", "Cultural Heritage", "2024-01-18"),    // row 6: contradiction
        Row("MA/2024/103", "FRA", "N", "Cultural Heritage", "2024-01-18"),    // row 7: contradiction
        Row("MA/2024/104", "FRN", "X", "Ocean Plastic", "2024-02-06"),        // row 8: invalid code
        Row("MA/2024/104", "NZL", "Y", "Ocean Plastic", "2024-02-06"),        // row 9: not a member
    ];

    /// <summary>The corrected file of section 7.2.</summary>
    public static string[] RevisedFile() =>
    [
        Row("MA/2024/101", "BRA", "A", "Maritime Safety: Revised", published: "2024-04-20T09:00:00Z"), // conflict + title conflict
        Row("MA/2024/101", "IND", "Y", "Maritime Safety: Revised", published: "2024-04-20T09:00:00Z"), // unchanged
        Row("MA/2024/103", "FRA", "N", "Cultural Heritage", "2024-01-18", published: "2024-04-20T09:00:00Z"), // new
        Row("MA/2024/104", "FRA", "X", "Ocean Plastic", "2024-02-06", published: "2024-04-20T09:00:00Z"),     // new (completion)
        Row("MA/2024/105", "BRA", "Y", "Climate Adaptation Finance", "2024-03-05", published: "2024-04-20T09:00:00Z"), // new vote
    ];
}
