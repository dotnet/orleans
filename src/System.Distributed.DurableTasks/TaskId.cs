using System.Diagnostics.CodeAnalysis;

namespace System.Distributed.DurableTasks;

/// <summary>Identifies a durable task within a hierarchy.</summary>
public readonly struct TaskId : IEquatable<TaskId>, IParsable<TaskId>, ISpanParsable<TaskId>, ISpanFormattable
{
    private readonly HierarchicalKey? _key;

    private TaskId(HierarchicalKey key) => _key = key;

    /// <summary>Gets the empty identifier.</summary>
    public static TaskId None => default;

    /// <summary>Gets a value indicating whether this identifier is empty.</summary>
    public bool IsDefault => _key is null;

    /// <summary>Creates a root identifier from one logical segment.</summary>
    public static TaskId CreateRoot(string rootSegment) => new(HierarchicalKey.FromSegment(rootSegment));

    /// <summary>Creates a child identifier by appending one logical segment.</summary>
    public TaskId Child(string segment)
    {
        if (_key is null)
        {
            throw new InvalidOperationException("A child identifier requires a non-empty parent.");
        }

        return new(_key.AppendSegment(segment));
    }

    /// <summary>Gets the parent identifier, or <see cref="None"/> for a root.</summary>
    public TaskId Parent() => _key?.Parent is { } parent ? new(parent) : None;

    /// <summary>Returns whether this identifier is an ancestor of <paramref name="other"/>.</summary>
    public bool IsAncestorOf(TaskId other) => _key?.IsAncestorOf(other._key) is true;
    /// <summary>Returns whether this identifier is a descendant of <paramref name="other"/>.</summary>
    public bool IsDescendantOf(TaskId other) => other.IsAncestorOf(this);
    /// <summary>Returns whether this identifier is the parent of <paramref name="other"/>.</summary>
    public bool IsParentOf(TaskId other) => _key?.IsParentOf(other._key) is true;
    /// <summary>Returns whether this identifier is a child of <paramref name="other"/>.</summary>
    public bool IsChildOf(TaskId other) => other.IsParentOf(this);

    /// <summary>Parses an escaped hierarchical path.</summary>
    public static TaskId Parse(string s, IFormatProvider? provider = null) => new(HierarchicalKey.Parse(s));

    /// <summary>Attempts to parse an escaped hierarchical path.</summary>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TaskId result)
    {
        if (HierarchicalKey.TryParse(s, out var key))
        {
            result = new(key);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Parses an escaped hierarchical path.</summary>
    public static TaskId Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => new(HierarchicalKey.Parse(s));

    /// <summary>Attempts to parse an escaped hierarchical path.</summary>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out TaskId result)
    {
        if (HierarchicalKey.TryParse(s, out var key))
        {
            result = new(key);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Formats this identifier as an escaped hierarchical path.</summary>
    public override string ToString() => _key?.ToString() ?? string.Empty;
    /// <summary>Formats this identifier as an escaped hierarchical path.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Attempts to format this identifier into <paramref name="destination"/>.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        var value = _key?.ToString() ?? string.Empty;
        if (value.AsSpan().TryCopyTo(destination))
        {
            charsWritten = value.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(TaskId other) => Equals(_key, other._key);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TaskId other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => _key?.GetHashCode() ?? 0;
    /// <summary>Returns whether two identifiers are equal.</summary>
    public static bool operator ==(TaskId left, TaskId right) => left.Equals(right);
    /// <summary>Returns whether two identifiers differ.</summary>
    public static bool operator !=(TaskId left, TaskId right) => !left.Equals(right);
    /// <summary>Formats an identifier as an escaped hierarchical path.</summary>
    public static explicit operator string(TaskId value) => value.ToString();
    /// <summary>Parses an escaped hierarchical path.</summary>
    public static explicit operator TaskId(string value) => Parse(value);
}

internal sealed class HierarchicalKey : IEquatable<HierarchicalKey>
{
    private const char Separator = '/';
    private const char Escape = '\\';
    private readonly string[] _segments;

    private HierarchicalKey(string[] segments) => _segments = segments;

    public HierarchicalKey? Parent => _segments.Length > 1 ? new(_segments[..^1]) : null;

    public static HierarchicalKey FromSegment(string segment)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);
        return new([segment]);
    }

    public HierarchicalKey AppendSegment(string segment)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);
        return new([.. _segments, segment]);
    }

    public bool IsAncestorOf(HierarchicalKey? other)
        => other is not null
            && _segments.Length <= other._segments.Length
            && _segments.AsSpan().SequenceEqual(other._segments.AsSpan(0, _segments.Length));

    public bool IsParentOf(HierarchicalKey? other)
        => other is not null && other._segments.Length == _segments.Length + 1 && IsAncestorOf(other);

    public static HierarchicalKey Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value, out var result)
            ? result
            : throw new FormatException("The task identifier is not a valid escaped hierarchical path.");
    }

    public static HierarchicalKey Parse(ReadOnlySpan<char> value)
        => TryParse(value, out var result)
            ? result
            : throw new FormatException("The task identifier is not a valid escaped hierarchical path.");

    public static bool TryParse(
        [NotNullWhen(true)] string? value,
        [NotNullWhen(true)] out HierarchicalKey? result)
        => TryParse(value.AsSpan(), out result);

    public static bool TryParse(ReadOnlySpan<char> value, [NotNullWhen(true)] out HierarchicalKey? result)
    {
        result = null;
        if (value.IsEmpty)
        {
            return false;
        }

        var segments = new List<string>();
        var segment = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                if (character is not Separator and not Escape)
                {
                    return false;
                }

                segment.Append(character);
                escaped = false;
            }
            else if (character == Escape)
            {
                escaped = true;
            }
            else if (character == Separator)
            {
                if (segment.Length == 0)
                {
                    return false;
                }

                segments.Add(segment.ToString());
                segment.Clear();
            }
            else
            {
                segment.Append(character);
            }
        }

        if (escaped || segment.Length == 0)
        {
            return false;
        }

        segments.Add(segment.ToString());
        result = new(segments.ToArray());
        return true;
    }

    public override string ToString() => string.Join(Separator, _segments.Select(EscapeSegment));

    private static string EscapeSegment(string segment)
        => segment.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("/", "\\/", StringComparison.Ordinal);

    public bool Equals(HierarchicalKey? other)
        => other is not null && _segments.AsSpan().SequenceEqual(other._segments);

    public override bool Equals(object? obj) => obj is HierarchicalKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in _segments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
