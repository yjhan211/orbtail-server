using System.Text;

namespace ops_server.services;

public sealed class StoryletCsvService
{
    private const string StartFile = "mission_storylet_start.csv";
    private const string PoolFile = "mission_storylet_pool.csv";
    private const string AreaFile = "area_name.csv";
    private const string InteractableFile = "interactable_info.csv";
    private const string ObjectActionFile = "object_action.csv";
    private const string GraphNodeFile = "mission_graph_node.csv";
    private const string GraphRecipeFile = "mission_graph_recipe.csv";
    private const string ItemInfoFile = "item_info.csv";
    private const string LocalizationFile = "localization.csv";

    // 로컬라이징 분리 대상 prefix(전 파일 합집합). 행에 {prefix}_key가 없으면 자동 스킵되므로 단일 목록으로 처리.
    private static readonly string[] LocalizedPrefixes =
    {
        "title", "success_text", "short_name", "description",
        "action_text", "result_text", "alibi_claim", "visible_trace"
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _csvRoot;

    public StoryletCsvService(IConfiguration configuration)
    {
        string? configuredRoot = configuration["CsvRoot"];
        _csvRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : ResolveCsvRoot();
    }

    public StoryletCsvBundle Load()
    {
        var starts = ReadTable(StartFile);
        var pools = ReadTable(PoolFile);
        var areas = ReadTable(AreaFile);
        var interactables = ReadTable(InteractableFile);
        var objectActions = ReadTable(ObjectActionFile);
        var graphNodes = ReadTable(GraphNodeFile);
        var graphRecipes = ReadTable(GraphRecipeFile);
        var items = ReadTable(ItemInfoFile);

        // {prefix}_key를 localization 텍스트로 해석해 에디터에 kr/en/jp로 투명 노출.
        var localization = ReadLocalizationDict();
        foreach (var table in new[] { starts, pools, interactables, objectActions, graphNodes, graphRecipes })
            foreach (var row in table.Rows)
                InjectLocalizedText(row, localization);

        return new StoryletCsvBundle
        {
            CsvRoot = _csvRoot,
            Starts = starts.Rows,
            Pools = pools.Rows,
            Areas = areas.Rows,
            Interactables = interactables.Rows,
            ObjectActions = objectActions.Rows,
            GraphNodes = graphNodes.Rows,
            GraphRecipes = graphRecipes.Rows,
            Items = items.Rows,
        };
    }

    public StoryletCsvUpdateResult UpdateStart(string nodeId, Dictionary<string, string?> incoming)
    {
        SaveLocalizedFromIncoming(incoming);
        return UpdateRow(StartFile, "node_id", nodeId, incoming);
    }

    public StoryletCsvUpdateResult UpdatePool(string nodeId, Dictionary<string, string?> incoming)
    {
        // 텍스트(kr/en/jp)는 {prefix}_key로 localization.csv에 저장. 나머지 구조 컬럼은 pool.csv에 저장.
        SaveLocalizedFromIncoming(incoming);
        return UpdateRow(PoolFile, "node_id", nodeId, incoming);
    }

    public StoryletCsvUpdateResult UpdateInteractable(string id, Dictionary<string, string?> incoming)
    {
        SaveLocalizedFromIncoming(incoming);
        return UpdateRow(InteractableFile, "id", id, incoming);
    }

    public StoryletCsvUpdateResult UpdateItem(string id, Dictionary<string, string?> incoming)
        => UpdateRow(ItemInfoFile, "id", id, incoming);

    public StoryletCsvUpdateResult UpdateRecipe(string recipeId, Dictionary<string, string?> incoming)
    {
        SaveLocalizedFromIncoming(incoming);
        return UpdateRow(GraphRecipeFile, "recipe_id", recipeId, incoming);
    }

    public StoryletCsvUpdateResult UpdateObjectAction(string actionGroupKey, string actionId,
        Dictionary<string, string?> incoming)
    {
        SaveLocalizedFromIncoming(incoming);
        var table = ReadTable(ObjectActionFile);
        int rowIndex = table.Rows.FindIndex(row =>
        {
            string group = row.TryGetValue("action_group_key", out string? value) ? value : "";
            return group == actionGroupKey &&
                   row.TryGetValue("action_id", out string? currentActionId) &&
                   currentActionId == actionId;
        });

        if (rowIndex < 0)
        {
            return new StoryletCsvUpdateResult(
                false,
                $"{ObjectActionFile} action_group_key={actionGroupKey}, action_id={actionId} not found");
        }

        var existing = table.Rows[rowIndex];
        foreach (string header in table.Headers)
        {
            if (incoming.TryGetValue(header, out string? value))
            {
                existing[header] = value ?? "";
            }
        }

        WriteTable(ObjectActionFile, table);
        return new StoryletCsvUpdateResult(true, $"{ObjectActionFile} save complete");
    }

    private StoryletCsvUpdateResult UpdateRow(string fileName, string keyColumn, string keyValue,
        Dictionary<string, string?> incoming)
    {
        var table = ReadTable(fileName);
        int rowIndex = table.Rows.FindIndex(row =>
            row.TryGetValue(keyColumn, out string? value) && value == keyValue);

        if (rowIndex < 0)
        {
            return new StoryletCsvUpdateResult(false, $"{fileName}에서 {keyColumn}={keyValue} 행을 찾지 못했습니다.");
        }

        var existing = table.Rows[rowIndex];
        foreach (string header in table.Headers)
        {
            if (incoming.TryGetValue(header, out string? value))
            {
                existing[header] = value ?? "";
            }
        }

        WriteTable(fileName, table);
        return new StoryletCsvUpdateResult(true, $"{fileName} 저장 완료");
    }

    private CsvTable ReadTable(string fileName)
    {
        string path = Path.Combine(_csvRoot, fileName);
        string text = File.ReadAllText(path, Utf8NoBom);
        var records = ParseCsv(text);
        if (records.Count == 0) return new CsvTable([], []);

        var headers = records[0];
        var rows = new List<Dictionary<string, string>>();
        foreach (var record in records.Skip(1))
        {
            if (record.Count == 1 && string.IsNullOrEmpty(record[0])) continue;

            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < headers.Count; i++)
            {
                row[headers[i]] = i < record.Count ? record[i] : "";
            }
            rows.Add(row);
        }

        return new CsvTable(headers, rows);
    }

    private void WriteTable(string fileName, CsvTable table)
    {
        string path = Path.Combine(_csvRoot, fileName);
        string backupPath = $"{path}.bak";
        File.Copy(path, backupPath, overwrite: true);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", table.Headers.Select(EscapeCsv)));
        foreach (var row in table.Rows)
        {
            sb.AppendLine(string.Join(",", table.Headers.Select(header =>
                EscapeCsv(row.TryGetValue(header, out string? value) ? value : ""))));
        }

        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
    }

    // ── 로컬라이징(localization.csv) 투명 처리 ─────────────────────────────────

    private Dictionary<string, (string kr, string en, string jp)> ReadLocalizationDict()
    {
        var map = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        string path = Path.Combine(_csvRoot, LocalizationFile);
        if (!File.Exists(path)) return map;

        var table = ReadTable(LocalizationFile);
        foreach (var row in table.Rows)
        {
            string key = row.TryGetValue("text_key", out string? k) ? k.Trim() : "";
            if (key.Length == 0) continue;
            map[key] = (
                row.TryGetValue("kr", out string? kr) ? kr : "",
                row.TryGetValue("en", out string? en) ? en : "",
                row.TryGetValue("jp", out string? jp) ? jp : "");
        }

        return map;
    }

    // pool 행에 {prefix}_key가 있으면 해석된 텍스트를 {prefix}_kr/en/jp로 주입(에디터 표시·편집용).
    private static void InjectLocalizedText(
        Dictionary<string, string> row,
        Dictionary<string, (string kr, string en, string jp)> localization)
    {
        foreach (string prefix in LocalizedPrefixes)
        {
            if (!row.TryGetValue($"{prefix}_key", out string? key) || string.IsNullOrWhiteSpace(key))
                continue;
            if (!localization.TryGetValue(key.Trim(), out var text))
                continue;

            row[$"{prefix}_kr"] = text.kr;
            row[$"{prefix}_en"] = text.en;
            row[$"{prefix}_jp"] = text.jp;
        }
    }

    // 저장 시 들어온 {prefix}_kr/en/jp를 {prefix}_key 기준으로 localization.csv에 upsert.
    private void SaveLocalizedFromIncoming(Dictionary<string, string?> incoming)
    {
        var table = ReadTable(LocalizationFile);
        if (table.Headers.Count == 0) return;

        bool changed = false;
        foreach (string prefix in LocalizedPrefixes)
        {
            if (!incoming.TryGetValue($"{prefix}_key", out string? key) || string.IsNullOrWhiteSpace(key))
                continue;
            key = key.Trim();

            string kr = incoming.TryGetValue($"{prefix}_kr", out string? a) ? a ?? "" : "";
            string en = incoming.TryGetValue($"{prefix}_en", out string? b) ? b ?? "" : "";
            string jp = incoming.TryGetValue($"{prefix}_jp", out string? c) ? c ?? "" : "";

            int idx = table.Rows.FindIndex(r => r.TryGetValue("text_key", out string? t) && t == key);
            if (idx >= 0)
            {
                table.Rows[idx]["kr"] = kr;
                table.Rows[idx]["en"] = en;
                table.Rows[idx]["jp"] = jp;
            }
            else
            {
                table.Rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["text_key"] = key,
                    ["kr"] = kr,
                    ["en"] = en,
                    ["jp"] = jp
                });
            }

            changed = true;
        }

        if (changed) WriteTable(LocalizationFile, table);
    }

    private static string ResolveCsvRoot()
    {
        foreach (string seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string? cursor = Path.GetFullPath(seed);
            while (!string.IsNullOrEmpty(cursor))
            {
                string candidate = Path.Combine(cursor, "network", "Common", "csv");
                if (Directory.Exists(candidate)) return candidate;
                cursor = Directory.GetParent(cursor)?.FullName;
            }
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "network", "Common", "csv"));
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                    break;
                case '\n':
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    private sealed record CsvTable(List<string> Headers, List<Dictionary<string, string>> Rows);
}

public sealed class StoryletCsvBundle
{
    public string CsvRoot { get; init; } = "";
    public List<Dictionary<string, string>> Starts { get; init; } = [];
    public List<Dictionary<string, string>> Pools { get; init; } = [];
    public List<Dictionary<string, string>> Areas { get; init; } = [];
    public List<Dictionary<string, string>> Interactables { get; init; } = [];
    public List<Dictionary<string, string>> ObjectActions { get; init; } = [];
    public List<Dictionary<string, string>> GraphNodes { get; init; } = [];
    public List<Dictionary<string, string>> GraphRecipes { get; init; } = [];
    public List<Dictionary<string, string>> Items { get; init; } = [];
}

public sealed record StoryletCsvUpdateResult(bool Success, string Message);
