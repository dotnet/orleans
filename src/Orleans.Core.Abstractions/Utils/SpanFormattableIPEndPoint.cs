using System;
using System.Net;
using System.Net.Sockets;

#nullable enable
namespace Orleans;

internal readonly struct SpanFormattableIPEndPoint : ISpanFormattable
{
    private readonly IPEndPoint? _value;

    public SpanFormattableIPEndPoint(IPEndPoint? value) => _value = value;

    public override string ToString() => $"{this}";
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_value is null)
        {
            charsWritten = 0;
            return true;
        }

        return _value.Address.AddressFamily == AddressFamily.InterNetworkV6
            ? destination.TryWrite($"[{new SpanFormattableIPAddress(_value.Address)}]:{_value.Port}", out charsWritten)
            : destination.TryWrite($"{new SpanFormattableIPAddress(_value.Address)}:{_value.Port}", out charsWritten);
    }
}

internal readonly struct SpanFormattableIPAddress : ISpanFormattable
{
    private readonly IPAddress _value;

    public SpanFormattableIPAddress(IPAddress value) => _value = value;

    public override string ToString() => _value.ToString();
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) => _value.TryFormat(destination, out charsWritten);
}
