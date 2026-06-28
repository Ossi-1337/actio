using System.Globalization;

namespace Actio.Core.Expressions;

public enum ExpressionValueKind
{
    Null,
    Boolean,
    Number,
    String
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

    public bool AsBoolean()
    {
        return Kind switch
        {
            ExpressionValueKind.Boolean => (bool)Value!,
            ExpressionValueKind.Number => (decimal)Value! != 0,
            ExpressionValueKind.String => !string.IsNullOrEmpty((string)Value!),
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
}
