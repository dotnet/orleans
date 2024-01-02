using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents the address of a <see cref="IDurableTaskObserver"/>.
/// </summary>
[Serializable, GenerateSerializer, Immutable]
[Alias("DurableTaskClientAddress")]
public readonly struct DurableTaskObserverAddress : IEquatable<DurableTaskObserverAddress>, IComparable<DurableTaskObserverAddress>, ISpanFormattable, IParsable<DurableTaskObserverAddress>
{
    [Id(0)]
    private readonly IdSpan _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableTaskObserverAddress"/> struct. 
    /// </summary>
    /// <param name="value">
    /// The value.
    /// </param>
    public DurableTaskObserverAddress(IdSpan value)
    {
        _value = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableTaskObserverAddress"/> struct. 
    /// </summary>
    /// <param name="value">
    /// The raw id value.
    /// </param>
    public DurableTaskObserverAddress(byte[] value)
    {
        _value = new(value);
    }

    /// <summary>
    /// Gets the underlying value.
    /// </summary>
    public IdSpan Value => _value;

    /// <summary>
    /// Returns a span representation of this instance.
    /// </summary>
    /// <returns>
    /// A <see cref="ReadOnlySpan{Byte}"/> representation of the value.
    /// </returns>
    public ReadOnlySpan<byte> AsSpan() => _value.AsSpan();

    /// <summary>
    /// Creates a new <see cref="DurableTaskObserverAddress"/> instance.
    /// </summary>
    /// <param name="value">
    /// The value.
    /// </param>
    /// <returns>
    /// The newly created <see cref="DurableTaskObserverAddress"/> instance.
    /// </returns>
    public static DurableTaskObserverAddress Create(string value) => new(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Converts a <see cref="DurableTaskObserverAddress"/> to a <see cref="IdSpan"/>.
    /// </summary>
    /// <param name="kind">The grain type to convert.</param>
    /// <returns>The corresponding <see cref="IdSpan"/>.</returns>
    public static explicit operator IdSpan(DurableTaskObserverAddress kind) => kind._value;

    /// <summary>
    /// Converts a <see cref="IdSpan"/> to a <see cref="DurableTaskObserverAddress"/>.
    /// </summary>
    /// <param name="id">The id span to convert.</param>
    /// <returns>The corresponding <see cref="DurableTaskObserverAddress"/>.</returns>
    public static explicit operator DurableTaskObserverAddress(IdSpan id) => new(id);

    /// <summary>
    /// Gets a value indicating whether this instance is the default value.
    /// </summary>
    public bool IsDefault => _value.IsDefault;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DurableTaskObserverAddress kind && Equals(kind);

    /// <inheritdoc/>
    public bool Equals(DurableTaskObserverAddress obj) => _value.Equals(obj._value);

    /// <inheritdoc/>
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>
    /// Generates a uniform, stable hash code for this grain type. 
    /// </summary>
    /// <returns>
    /// A uniform, stable hash of this instance.
    /// </returns>
    public uint GetUniformHashCode() => _value.GetUniformHashCode();

    /// <summary>
    /// Returns the array underlying a grain type instance.
    /// </summary>
    /// <param name="id">The grain type.</param>
    /// <returns>The array underlying a grain type instance.</returns>
    /// <remarks>
    /// The returned array must not be modified.
    /// </remarks>
    public static byte[]? UnsafeGetArray(DurableTaskObserverAddress id) => IdSpan.UnsafeGetArray(id._value);

    /// <inheritdoc/>
    public int CompareTo(DurableTaskObserverAddress other) => _value.CompareTo(other._value);

    /// <summary>
    /// Returns a string representation of this instance, decoding the value as UTF8.
    /// </summary>
    /// <returns>
    /// A <see cref="string"/> representation of this instance.
    /// </returns>
    public override string ToString() => _value.ToString();

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString() ?? "";

    bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => _value.TryFormat(destination, out charsWritten);

    public static DurableTaskObserverAddress Parse(string s, IFormatProvider? provider) => Create(s);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out DurableTaskObserverAddress result)
    {
        if (s is { Length: > 0 })
        {
            result = Create(s);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Compares the provided operands for equality.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
    public static bool operator ==(DurableTaskObserverAddress left, DurableTaskObserverAddress right) => left.Equals(right);

    /// <summary>
    /// Compares the provided operands for inequality.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
    public static bool operator !=(DurableTaskObserverAddress left, DurableTaskObserverAddress right) => !(left == right);
}
