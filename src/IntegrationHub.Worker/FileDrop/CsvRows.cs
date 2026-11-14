using System.Globalization;
using CsvHelper;
using FluentValidation.Results;

namespace IntegrationHub.Worker.FileDrop;

public sealed record RowError(int Line, string Message);

/// <summary>Written next to every processed file as &lt;file&gt;.report.json.</summary>
public sealed class ImportReport
{
    public int TotalRows { get; set; }

    public int ImportedRows { get; set; }

    public string? FileError { get; set; }

    public string? Summary { get; set; }

    public List<RowError> Errors { get; } = [];

    public void AddErrors(int line, ValidationResult result) =>
        Errors.AddRange(result.Errors.Select(e => new RowError(line, e.ErrorMessage)));
}

/// <summary>One CSV data row as raw strings, keyed by column name. Missing optional columns read as null.</summary>
public sealed class CsvRow(IReadOnlyDictionary<string, string?> fields)
{
    public string? this[string column] => fields.TryGetValue(column, out var value) && !string.IsNullOrEmpty(value) ? value : null;
}

internal static class CsvRows
{
    /// <summary>
    /// Reads every data row as raw strings with its physical line number. Type conversion is left to the
    /// validators on purpose: a bad value in one row must become a row error, not abort the whole file.
    /// </summary>
    public static List<(int Line, CsvRow Row)> Read(TextReader reader, IReadOnlyList<string> required, IReadOnlyList<string> optional, ImportReport report)
    {
        if (reader.Peek() < 0)
        {
            report.FileError = "File is empty.";
            return [];
        }

        var malformedLines = new HashSet<int>();
        var options = new CsvReaderOptions
        {
            TrackLineNumbers = true,
            TrimWhitespace = true,
            AllowLineBreaksInQuotes = false, // one stray quote must not swallow the rest of the file
            OnMalformedRecord = bad => malformedLines.Add((int)bad.LineNumber),
        };

        using var csv = CsvReader.Create(reader, options, leaveOpen: true);
        csv.ReadHeader();

        var missing = required.Where(name => !csv.TryGetFieldIndex(name, out _)).ToList();
        if (missing.Count > 0)
        {
            report.FileError = "Missing required column(s): " + string.Join(", ", missing);
            return [];
        }

        var columns = required.Concat(optional)
            .Select(name => (Name: name, Index: csv.TryGetFieldIndex(name, out var index) ? index : -1))
            .ToList();

        var rows = new List<(int, CsvRow)>();
        while (csv.Read())
        {
            report.TotalRows++;
            var line = (int)csv.LineNumber;
            if (malformedLines.Contains(line))
            {
                report.Errors.Add(new RowError(line, "Malformed CSV row (check quoting)."));
                continue;
            }

            var fields = columns.ToDictionary(
                c => c.Name,
                c => c.Index >= 0 && c.Index < csv.FieldCount ? csv.GetString(c.Index) : null);
            rows.Add((line, new CsvRow(fields)));
        }

        return rows;
    }

    public static bool TryParseDecimal(string? value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
}
