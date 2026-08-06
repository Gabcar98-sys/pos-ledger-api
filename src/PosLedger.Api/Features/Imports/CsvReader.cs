using System.Text;

namespace PosLedger.Api.Features.Imports;

public sealed record CsvRow(int LineNumber, IReadOnlyList<string> Fields, string Raw);

/// <summary>
/// A small RFC 4180 reader: quoted fields, escaped quotes (<c>""</c>), embedded commas and
/// newlines, and both LF and CRLF endings.
/// <para>
/// Hand-written rather than pulled from a package because the job here is not "read a CSV", it is
/// "report the line number and original text of every row that was wrong". A reader that yields
/// records without carrying those two things cannot produce the report this endpoint exists for.
/// </para>
/// <para>
/// The file is read into memory in one go. Import files here are order-of-megabytes and every row
/// has to be held anyway to be checked against the catalogue, so streaming would buy nothing and
/// cost the line numbers. The upload size is capped by the endpoint.
/// </para>
/// </summary>
public static class CsvReader
{
    public static async Task<IReadOnlyList<CsvRow>> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);

        return Parse(text);
    }

    public static IReadOnlyList<CsvRow> Parse(string text)
    {
        var rows = new List<CsvRow>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var raw = new StringBuilder();

        var lineNumber = 1;
        var inQuotes = false;

        void EndRow()
        {
            fields.Add(field.ToString());

            // A line that is entirely empty is skipped rather than reported: trailing blank lines
            // are what every spreadsheet export ends with, and calling them errors is noise.
            if (fields.Count > 1 || fields[0].Length > 0)
            {
                rows.Add(new CsvRow(lineNumber, [.. fields], raw.ToString().TrimEnd('\r', '\n')));
            }

            fields.Clear();
            field.Clear();
            raw.Clear();
            lineNumber++;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            raw.Append(c);

            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    raw.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    break; // the \n that follows ends the row

                case '\n':
                    EndRow();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0 || raw.Length > 0)
        {
            EndRow();
        }

        return rows;
    }
}
