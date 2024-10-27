using System.Text.Json;

public class LocalizedText
{
    public LocalizedText(string jsonString)
    {
        try
        {
            jsonString = jsonString.Trim('"');
            jsonString = jsonString.Replace("\"\"", "\"");

            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);
            Kr = dict!["kr"];
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse LocalizedText. Raw input: '{jsonString}'", ex);
        }
    }

    public string Kr { get; }

    public override string ToString()
    {
        return Kr;
    }
}