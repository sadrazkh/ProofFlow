using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Contracts.Capture;
using ProofFlow.Domain.Data;
using ProofFlow.Infrastructure.Persistence;

namespace ProofFlow.Infrastructure.Data;

/// <summary>
/// Reading rows in, and freezing them into versions.
///
/// The import half of this exists because of how the data actually arrives: pasted out of a
/// spreadsheet, or out of a database client, or out of a chat message where somebody listed forty
/// identifiers one per line. Asking a non-programmer to convert that into a particular format
/// before the tool will accept it is asking them to do the parsing by hand.
/// </summary>
public sealed class DataSetService(ProofFlowDbContext db, ICurrentUser me, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Works out what was pasted and reads it, without importing anything.
    ///
    /// Nothing is stored here on purpose. The parser guesses, and a guess about somebody's data
    /// has to be shown to them before it becomes rows — the difference between a comma-separated
    /// file and a one-per-line list is one comma inside one value, and getting it wrong silently
    /// produces a set that runs perfectly against the wrong inputs.
    /// </summary>
    public static ParsedPasteDto Parse(string? text, string? preferredFormat = null)
    {
        var raw = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = raw.Split('\n');
        var nonEmpty = lines.Where(line => line.Trim().Length > 0).ToArray();

        if (nonEmpty.Length == 0)
        {
            return new ParsedPasteDto { Format = "Empty", Columns = [], Rows = [], TotalLines = 0 };
        }

        var format = preferredFormat ?? Detect(nonEmpty);

        return format switch
        {
            "Json" => ParseJson(raw, lines.Length),
            "Tsv" => ParseDelimited(lines, '\t', "Tsv"),
            "Csv" => ParseDelimited(lines, ',', "Csv"),
            _ => ParseLines(lines),
        };
    }

    /// <summary>
    /// Guesses the format.
    ///
    /// Order matters. JSON is checked first because a JSON array of objects contains commas and
    /// would otherwise read as a very strange CSV. Tabs beat commas because a tab inside a value
    /// is far rarer than a comma inside one, so a line holding both is almost certainly
    /// tab-separated data with commas in its text.
    ///
    /// It looks for agreement among most lines rather than all of them, and counts delimiters
    /// outside quotes. Demanding unanimity on the raw count sounds stricter and is worse in both
    /// directions: a quoted comma inside one value made a real table read as a plain list, and so
    /// did a single row with a missing cell — the case the problem list exists to report.
    /// </summary>
    private static string Detect(string[] lines)
    {
        var first = lines[0].TrimStart();
        if (first.StartsWith('[') || first.StartsWith('{')) return "Json";

        if (Agrees(lines, '\t')) return "Tsv";
        if (Agrees(lines, ',')) return "Csv";

        return "Lines";
    }

    /// <summary>
    /// True when most lines hold the same non-zero number of unquoted delimiters.
    ///
    /// A majority rather than all: one line out of forty with a comma in it is a name in a plain
    /// list, and thirty-nine out of forty agreeing is a table with one broken row.
    /// </summary>
    private static bool Agrees(string[] lines, char delimiter)
    {
        var modal = lines
            .Select(line => Unquoted(line, delimiter))
            .GroupBy(count => count)
            .OrderByDescending(group => group.Count())
            .First();

        return modal.Key > 0 && modal.Count() * 2 >= lines.Length;
    }

    private static int Unquoted(string line, char delimiter)
    {
        var count = 0;
        var quoted = false;

        foreach (var character in line)
        {
            if (character == '"') quoted = !quoted;
            else if (character == delimiter && !quoted) count++;
        }

        return count;
    }

    private static ParsedPasteDto ParseJson(string raw, int totalLines)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            return new ParsedPasteDto
            {
                Format = "Json", Columns = [], Rows = [], TotalLines = totalLines,
                Problems = [new PasteProblem(0, string.Empty, ex.Message)],
            };
        }

        var array = node as JsonArray ?? (node is null ? [] : [node]);
        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var problems = new List<PasteProblem>();

        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item)
            {
                problems.Add(new PasteProblem(index + 1, array[index]?.ToJsonString() ?? "null",
                    "Not an object, so it has no columns."));
                continue;
            }

            var row = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (name, value) in item)
            {
                if (!columns.Contains(name)) columns.Add(name);

                // Scalars unquoted, everything else as JSON: a nested object is legitimate input
                // and turning it into "System.Text.Json.Nodes.JsonObject" would lose it.
                row[name] = value is JsonValue scalar
                    ? scalar.ToString()
                    : value?.ToJsonString() ?? string.Empty;
            }

            rows.Add(row);
        }

        return new ParsedPasteDto
        {
            Format = "Json", Columns = columns, Rows = rows,
            Problems = problems, TotalLines = totalLines,
        };
    }

    private static ParsedPasteDto ParseDelimited(string[] lines, char delimiter, string format)
    {
        var body = lines.Where(line => line.Trim().Length > 0).ToArray();
        if (body.Length == 0) return new ParsedPasteDto { Format = format, Columns = [], Rows = [] };

        var columns = Split(body[0], delimiter)
            .Select((name, index) => name.Trim().Length > 0 ? name.Trim() : $"column{index + 1}")
            .ToList();

        var rows = new List<IReadOnlyDictionary<string, string>>();
        var problems = new List<PasteProblem>();

        for (var index = 1; index < body.Length; index++)
        {
            var cells = Split(body[index], delimiter);

            if (cells.Length != columns.Count)
            {
                // Said out loud with the line, not padded and not dropped. A row with the wrong
                // number of cells is somebody's data going missing quietly if it is padded.
                problems.Add(new PasteProblem(index + 1, body[index],
                    $"Has {cells.Length} values where the header has {columns.Count}."));
                continue;
            }

            rows.Add(columns
                .Select((name, position) => (name, value: cells[position].Trim()))
                .ToDictionary(pair => pair.name, pair => pair.value, StringComparer.Ordinal));
        }

        return new ParsedPasteDto
        {
            Format = format, Columns = columns, Rows = rows,
            Problems = problems, TotalLines = lines.Length,
        };
    }

    /// <summary>
    /// Splits one line, honouring double quotes.
    ///
    /// Written rather than taken from a library because the whole parser is fifty lines and a CSV
    /// dependency for this would be the larger commitment. It handles the case that actually
    /// occurs — a quoted value containing the delimiter — and doubles inside quotes as an escape.
    /// </summary>
    private static string[] Split(string line, char delimiter)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];

            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
                continue;
            }

            if (character == delimiter && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            cell.Append(character);
        }

        cells.Add(cell.ToString());
        return [.. cells];
    }

    /// <summary>One value per line — the commonest paste there is, and the one with no header.</summary>
    private static ParsedPasteDto ParseLines(string[] lines)
    {
        var rows = lines
            .Where(line => line.Trim().Length > 0)
            .Select(line => (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = line.Trim() })
            .ToList();

        return new ParsedPasteDto
        {
            Format = "Lines", Columns = ["value"], Rows = rows, TotalLines = lines.Length,
        };
    }

    /// <summary>
    /// Freezes a draft into the next version.
    ///
    /// Always the next one, never an edit of the last: a version anything has run against has to
    /// keep saying what it held, or every report older than the edit becomes fiction.
    /// </summary>
    public async Task<DataSetVersion> SaveVersionAsync(
        DataSet set, DataSetDraft draft, CancellationToken cancellationToken = default)
    {
        var numbers = await db.DataSetVersions
            .Where(v => v.DataSetId == set.Id)
            .Select(v => v.Number)
            .ToListAsync(cancellationToken);

        var keyColumn = draft.KeyColumn is { Length: > 0 } named && draft.Columns.Contains(named)
            ? named
            : null;

        var version = new DataSetVersion
        {
            WorkspaceId = set.WorkspaceId,
            DataSetId = set.Id,
            Number = numbers.Count == 0 ? 1 : numbers.Max() + 1,
            ColumnsJson = JsonSerializer.Serialize(draft.Columns, Json),
            Description = draft.Description,
            RowCount = draft.Rows.Count,
            CreatedByUserId = me.UserId ?? Guid.Empty,
        };

        db.DataSetVersions.Add(version);

        var ordinal = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in draft.Rows)
        {
            var key = keyColumn is not null && row.TryGetValue(keyColumn, out var value) && value.Length > 0
                ? value
                : ordinal.ToString();

            // A duplicate key would mean two approved answers for one input, and the baseline can
            // only hold one. Suffixed rather than refused: the rows are still worth keeping, and
            // the interface reports the count.
            if (!seen.Add(key)) key = $"{key}#{ordinal}";

            db.DataSetRows.Add(new DataSetRow
            {
                WorkspaceId = set.WorkspaceId,
                DataSetVersionId = version.Id,
                Ordinal = ordinal++,
                Key = key,
                ValuesJson = JsonSerializer.Serialize(row, Json),
            });
        }

        set.KeyColumn = keyColumn;
        set.CurrentVersionId = version.Id;
        set.UpdatedAt = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return version;
    }

    /// <summary>The rows of a version, as the editor holds them.</summary>
    public async Task<DataSetDraft> ReadAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await db.DataSetVersions
            .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);

        if (version is null) return new DataSetDraft();

        var rows = await db.DataSetRows
            .Where(r => r.DataSetVersionId == versionId)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.ValuesJson)
            .ToListAsync(cancellationToken);

        var set = await db.DataSets.FirstOrDefaultAsync(d => d.Id == version.DataSetId, cancellationToken);

        return new DataSetDraft
        {
            Columns = version.ColumnsJson is null
                ? []
                : JsonSerializer.Deserialize<List<string>>(version.ColumnsJson, Json) ?? [],
            Rows = [.. rows.Select(json =>
                (IReadOnlyDictionary<string, string>)(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json)
                    ?? new Dictionary<string, string>()))],
            KeyColumn = set?.KeyColumn,
            Description = version.Description,
        };
    }
}
