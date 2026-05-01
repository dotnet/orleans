namespace Orleans.Journaling;

internal record class LogFormatKey(string Value)
{
    public string Value { get; } = LogFormatServices.ValidateLogFormatKey(Value);
}
