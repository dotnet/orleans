using System.ComponentModel;
using System.Globalization;

namespace Orleans.AdvancedReminders;

internal sealed class ReminderRetryPolicyConverter : TypeConverter
{
    public ReminderRetryPolicyConverter() { }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string text)
        {
            return base.ConvertFrom(context, culture, value);
        }

        text = text.Trim();
        if (text.Equals(nameof(ReminderRetryPolicy.NoRetries), StringComparison.OrdinalIgnoreCase))
        {
            return ReminderRetryPolicy.NoRetries;
        }

        if (text.Equals(nameof(ReminderRetryPolicy.UntilSuccess), StringComparison.OrdinalIgnoreCase))
        {
            return ReminderRetryPolicy.UntilSuccess;
        }

        const string prefix = nameof(ReminderRetryPolicy.Limited) + "(";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && text.EndsWith(')')
            && int.TryParse(text.AsSpan(prefix.Length, text.Length - prefix.Length - 1).Trim(),
                NumberStyles.None, CultureInfo.InvariantCulture, out var maximumAttempts)
            && maximumAttempts > 0)
        {
            return ReminderRetryPolicy.Limited(maximumAttempts);
        }

        throw new FormatException("Expected NoRetries, UntilSuccess, or Limited(<positive attempt count>).");
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        => destinationType == typeof(string) && value is ReminderRetryPolicy policy
            ? policy.ToString()
            : base.ConvertTo(context, culture, value, destinationType);
}
