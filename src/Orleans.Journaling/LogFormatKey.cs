namespace Orleans.Journaling;

internal sealed class LogFormatKey
{
    public LogFormatKey(string value)
    {
        Value = LogFormatServices.ValidateLogFormatKey(value);
    }

    public string Value { get; }
}
