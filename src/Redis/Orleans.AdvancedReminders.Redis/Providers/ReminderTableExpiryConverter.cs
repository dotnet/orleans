using System.ComponentModel;
using System.Globalization;

namespace Orleans.AdvancedReminders.Redis;

internal sealed class ReminderTableExpiryConverter : TypeConverter
{
    public ReminderTableExpiryConverter() { }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value is string text ? ReminderTableExpiry.Parse(text) : base.ConvertFrom(context, culture, value);

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        => destinationType == typeof(string) && value is ReminderTableExpiry expiry
            ? expiry.ToString()
            : base.ConvertTo(context, culture, value, destinationType);
}
