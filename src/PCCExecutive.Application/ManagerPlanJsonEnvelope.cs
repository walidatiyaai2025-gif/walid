using System.Text;
using System.Text.Json;

namespace PCCExecutive.Application;

/// <summary>
/// Extracts exactly one top-level JSON object from a Manager response while tolerating
/// harmless ChatGPT presentation wrappers (for example prose, parentheses, or markdown fences).
/// Nested objects are consumed as part of their containing object, and multiple top-level JSON
/// objects remain ambiguous and therefore fail closed.
/// </summary>
internal static class ManagerPlanJsonEnvelope
{
    public static string ExtractSinglePlanObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new JsonException("Manager output is empty.");

        var candidates = new List<string>();
        var cursor = 0;
        while (cursor < content.Length)
        {
            var start = content.IndexOf('{', cursor);
            if (start < 0) break;

            if (TryReadObject(content[start..], out var json))
            {
                candidates.Add(json);
                cursor = start + Math.Max(1, json.Length);
                continue;
            }

            cursor = start + 1;
        }

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new JsonException("Manager output does not contain one valid structured JSON object."),
            _ => throw new JsonException("Manager output contains multiple top-level JSON objects; exactly one structured plan object is required.")
        };
    }

    private static bool TryReadObject(string source, out string json)
    {
        json = string.Empty;
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(source);
            var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object || reader.BytesConsumed <= 0)
                return false;

            json = Encoding.UTF8.GetString(utf8.AsSpan(0, checked((int)reader.BytesConsumed)));
            return !string.IsNullOrWhiteSpace(json);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
