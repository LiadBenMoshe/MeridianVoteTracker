using System.Text;

namespace Meridian.Core.Ingestion;

/// <summary>One physical CSV line, before validation. RowNo is the line number in the file (header = 1).</summary>
public sealed record RawRow(int RowNo, string Raw, IReadOnlyDictionary<string, string>? Fields, string? StructureError);

public sealed class CsvHeaderException(string message) : Exception(message);

/// <summary>
/// Streams an Assembly export (Appendix A). Columns are matched by header name.
/// Each line is parsed on its own (RFC 4180 quoting within the line), so a broken quote or a wrong
/// column count only affects that line and never the rest of the file (ING-07).
/// Exports never contain line breaks inside a field, so line = record.
/// </summary>
public static class CsvRowReader
{
    public static readonly string[] Columns =
    [
        "assembly_vote_id", "session", "vote_date", "resolution_title",
        "country_code", "country_name", "ballot", "published_at",
    ];

    public static IEnumerable<RawRow> Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (headerLine is null) throw new CsvHeaderException("The file is empty.");

        var fields = new List<string>();
        if (!TrySplit(headerLine.TrimStart('﻿'), fields, out _))
            throw new CsvHeaderException("The header line is not valid CSV.");
        var header = fields.Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var missing = Columns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0)
            throw new CsvHeaderException($"Missing column(s): {string.Join(", ", missing)}. Expected: {string.Join(",", Columns)}");
        var index = Columns.ToDictionary(c => c, c => Array.IndexOf(header, c));

        var lineNo = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!TrySplit(line, fields, out var error))
            {
                yield return new RawRow(lineNo, line, null, error);
                continue;
            }
            if (fields.Count != header.Length)
            {
                yield return new RawRow(lineNo, line, null, $"Expected {header.Length} columns, found {fields.Count}");
                continue;
            }

            var map = new Dictionary<string, string>(Columns.Length);
            foreach (var c in Columns) map[c] = fields[index[c]];
            yield return new RawRow(lineNo, line, map, null);
        }
    }

    /// <summary>Splits one CSV line. Quoted fields may contain commas and doubled quotes ("").</summary>
    public static bool TrySplit(string line, List<string> fields, out string? error)
    {
        fields.Clear();
        error = null;
        var i = 0;
        var sb = new StringBuilder();
        while (true)
        {
            if (i < line.Length && line[i] == '"')
            {
                sb.Clear();
                i++;
                while (true)
                {
                    if (i >= line.Length)
                    {
                        error = "Malformed CSV: unterminated quote";
                        return false;
                    }
                    var c = line[i];
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(c);
                    i++;
                }
                if (i < line.Length && line[i] != ',')
                {
                    error = "Malformed CSV: unexpected text after a closing quote";
                    return false;
                }
                fields.Add(sb.ToString());
            }
            else
            {
                var comma = line.IndexOf(',', i);
                var end = comma < 0 ? line.Length : comma;
                var value = line[i..end];
                if (value.Contains('"'))
                {
                    error = "Malformed CSV: quote inside an unquoted field";
                    return false;
                }
                fields.Add(value);
                i = end;
            }

            if (i >= line.Length) return true;
            i++; // skip comma
            if (i == line.Length)
            {
                fields.Add("");
                return true;
            }
        }
    }
}
