using System.Text.Json;

namespace Ntrx.Connector.Core.Search;

public sealed record InteractionSummary(Guid InteractionId, DateTimeOffset? StartTimeUtc, string RawJson);

/// <summary>
/// Parser tolerante do response do POST /Search/Interactions.
/// O schema exato do response não está confirmado (validar com a NICE) — este parser aceita
/// tanto um array na raiz quanto um objeto contendo um array de interações (inclusive aninhado
/// um nível), identificando cada interação por uma propriedade de id em formato GUID.
/// </summary>
public static class SearchResultParser
{
    private static readonly string[] IdPropertyNames = ["interactionId", "InteractionId", "id", "Id"];
    private static readonly string[] StartTimePropertyNames = ["startTime", "StartTime", "startTimeUtc"];

    public static IReadOnlyList<InteractionSummary> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var array = FindInteractionArray(doc.RootElement);
        if (array is null)
            return Array.Empty<InteractionSummary>();

        var result = new List<InteractionSummary>();
        foreach (var element in array.Value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            var id = ReadGuid(element);
            if (id is null)
                continue;
            result.Add(new InteractionSummary(id.Value, ReadStartTime(element), element.GetRawText()));
        }
        return result;
    }

    private static JsonElement? FindInteractionArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;
            if (property.Value.GetArrayLength() > 0
                && property.Value[0].ValueKind == JsonValueKind.Object
                && ReadGuid(property.Value[0]) is not null)
                return property.Value;
            if (string.Equals(property.Name, "interactions", StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;
            var nested = FindInteractionArray(property.Value);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static Guid? ReadGuid(JsonElement element)
    {
        foreach (var name in IdPropertyNames)
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var guid))
                return guid;
        return null;
    }

    private static DateTimeOffset? ReadStartTime(JsonElement element)
    {
        foreach (var name in StartTimePropertyNames)
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), out var parsed))
                return parsed;
        return null;
    }
}
