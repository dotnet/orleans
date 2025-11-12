namespace Orleans.Dashboard.Metrics.TypeFormatting;

internal readonly struct Token(TokenType type, string value)
{
    public TokenType Type { get; } = type;
    public string Value { get; } = value;
    public override string ToString() => $"{Type} = {Value}";
}
