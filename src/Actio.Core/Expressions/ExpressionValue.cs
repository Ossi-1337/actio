using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Actio.Core.Expressions;

public enum ExpressionValueKind
{
    Null,
    Boolean,
    Number,
    String,
    Json
}

public readonly record struct ExpressionValue(ExpressionValueKind Kind, object? Value)
{
    public static ExpressionValue Null { get; } = new(ExpressionValueKind.Null, null);

    public static ExpressionValue FromBoolean(bool value)
        => new(ExpressionValueKind.Boolean, value);

    public static ExpressionValue FromNumber(decimal value)
        => new(ExpressionValueKind.Number, value);

    public static ExpressionValue FromString(string value)
        => new(ExpressionValueKind.String, value);

    public static ExpressionValue FromJson(JsonNode value)
        => new(ExpressionValueKind.Json, value);

    public static ExpressionValue FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => Null,
            JsonValueKind.True => FromBoolean(true),
            JsonValueKind.False => FromBoolean(false),
            JsonValueKind.Number when element.TryGetDecimal(out var number) => FromNumber(number),
            JsonValueKind.String => FromString(element.GetString() ?? string.Empty),
            _ => FromJson(JsonNode.Parse(element.GetRawText())!)
        };
    }

    public bool AsBoolean()
    {
        return Kind switch
        {
            ExpressionValueKind.Boolean => (bool)Value!,
            ExpressionValueKind.Number => (decimal)Value! != 0,
            ExpressionValueKind.String => !string.IsNullOrEmpty((string)Value!),
            ExpressionValueKind.Json => true,
            _ => false
        };
    }

    public string AsString()
    {
        return Kind switch
        {
            ExpressionValueKind.Boolean => (bool)Value! ? "true" : "false",
            ExpressionValueKind.Number => ((decimal)Value!).ToString("0.#############################", CultureInfo.InvariantCulture),
            ExpressionValueKind.String => (string)Value!,
            ExpressionValueKind.Json => ((JsonNode)Value!).ToJsonString(ExpressionJson.SerializerOptions),
            _ => string.Empty
        };
    }

    public bool TryGetNumber(out decimal number)
    {
        if (Kind == ExpressionValueKind.Number)
        {
            number = (decimal)Value!;
            return true;
        }

        if (Kind == ExpressionValueKind.String &&
            decimal.TryParse((string)Value!, NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    public JsonNode? ToJsonNode()
    {
        return Kind switch
        {
            ExpressionValueKind.Null => null,
            ExpressionValueKind.Boolean => JsonValue.Create((bool)Value!),
            ExpressionValueKind.Number => JsonValue.Create((decimal)Value!),
            ExpressionValueKind.String => JsonValue.Create((string)Value!),
            ExpressionValueKind.Json => ((JsonNode)Value!).DeepClone(),
            _ => null
        };
    }
}

public static class ExpressionJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = false
    };
}
