using System.Text;

namespace network.common.data.helpers;

public static class CsvHelper
{
    public static Dictionary<string, CsvRow> LoadCsv(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"CSV file not found: {filePath}");

        var result = new Dictionary<string, CsvRow>();
        var lines = File.ReadAllLines(filePath, Encoding.UTF8);

        // Parse header
        var headers = ParseCsvLine(lines[0]);

        // Parse data rows
        for (var i = 1; i < lines.Length; i++)
        {
            var values = ParseCsvLine(lines[i]);
            if (values.Length != headers.Length)
                throw new InvalidDataException(
                    $"Line {i + 1}: Column count mismatch. Expected {headers.Length}, got {values.Length}");

            var row = new CsvRow(headers, values);
            result[values[0]] = row; // Assuming first column is ID
        }

        return result;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var currentField = new StringBuilder();
        var inQuotes = false;

        foreach (var currentChar in line)
            switch (currentChar)
            {
                case '"':
                    inQuotes = !inQuotes;
                    currentField.Append(currentChar); // 따옴표 보존
                    break;
                case ',' when !inQuotes:
                    fields.Add(currentField.ToString());
                    currentField.Clear();
                    break;
                default:
                    currentField.Append(currentChar);
                    break;
            }

        // Add the last field
        fields.Add(currentField.ToString());

        return fields.ToArray();
    }
}

public class CsvRow
{
    private readonly string[] _headers;
    private readonly string[] _values;

    public CsvRow(string[] headers, string[] values)
    {
        _headers = headers;
        _values = values;
    }

    public string this[string columnName]
    {
        get
        {
            var index = Array.IndexOf(_headers, columnName);
            if (index == -1)
                throw new KeyNotFoundException($"Column '{columnName}' not found");
            var value = _values[index];

            // 따옴표로 시작하는 값은 그대로 반환
            if (value.StartsWith("\"") && value.EndsWith("\"")) return value; // 따옴표 보존
            return value.Trim(); // 일반 값은 공백 제거
        }
    }
}