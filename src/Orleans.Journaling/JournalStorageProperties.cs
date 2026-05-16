using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Orleans.Journaling;

/// <summary>
/// Common journal storage property names and conventions.
/// </summary>
public static class JournalStoragePropertyNames
{
    /// <summary>
    /// Gets the prefix reserved for provider-owned property names.
    /// </summary>
    /// <remarks>
    /// Providers may expose properties with this prefix for diagnostics or coordination, but caller
    /// property updates must not set or remove provider-owned properties.
    /// </remarks>
    public const string ProviderReservedPrefix = "$";

    /// <summary>
    /// Determines whether <paramref name="propertyName"/> is provider-owned.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns><see langword="true"/> if the property is provider-owned; otherwise <see langword="false"/>.</returns>
    public static bool IsProviderOwned(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return propertyName.StartsWith(ProviderReservedPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// Immutable provider-visible coordination properties for a journal storage instance.
/// </summary>
/// <remarks>
/// Properties are string key/value pairs used for discovery and coordination. Durable application
/// state belongs in journal entries, not in these properties. Providers may include provider-owned
/// properties whose names begin with <see cref="JournalStoragePropertyNames.ProviderReservedPrefix"/>;
/// caller updates must preserve those properties.
/// </remarks>
public sealed class JournalStorageProperties
{
    /// <summary>
    /// Gets an empty property set with no ETag.
    /// </summary>
    public static JournalStorageProperties Empty { get; } = new(eTag: null, values: null);

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStorageProperties"/> class.
    /// </summary>
    /// <param name="eTag">The storage properties ETag, or <see langword="null"/> if none is available.</param>
    /// <param name="values">The property values.</param>
    public JournalStorageProperties(string? eTag, IReadOnlyDictionary<string, string>? values)
    {
        ETag = eTag;
        Values = CopyValues(values);
    }

    /// <summary>
    /// Gets the storage properties ETag, or <see langword="null"/> if none is available.
    /// </summary>
    public string? ETag { get; }

    /// <summary>
    /// Gets the string key/value properties.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>
    /// Creates a copy of this instance with a different ETag.
    /// </summary>
    /// <param name="eTag">The new ETag.</param>
    /// <returns>The updated properties.</returns>
    public JournalStorageProperties WithETag(string? eTag) => new(eTag, Values);

    private static ReadOnlyDictionary<string, string> CopyValues(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return new(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var result = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            ValidatePropertyName(key);
            ArgumentNullException.ThrowIfNull(value);
            result.Add(key, value);
        }

        return new(result);
    }

    internal static void ValidatePropertyName(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (propertyName.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Journal storage property names must not contain null characters.", nameof(propertyName));
        }
    }

    internal static void ValidateCallerPropertyName(string propertyName)
    {
        ValidatePropertyName(propertyName);
        if (JournalStoragePropertyNames.IsProviderOwned(propertyName))
        {
            throw new ArgumentException(
                $"Journal storage property '{propertyName}' is provider-owned. Caller updates must not set or remove provider-owned properties.",
                nameof(propertyName));
        }
    }
}

/// <summary>
/// Describes a caller-owned patch to journal storage properties.
/// </summary>
/// <remarks>
/// Providers apply updates atomically against the current property set and must preserve provider-owned
/// properties whose names begin with <see cref="JournalStoragePropertyNames.ProviderReservedPrefix"/>.
/// </remarks>
public sealed class JournalStoragePropertiesUpdate
{
    /// <summary>
    /// Gets an empty update.
    /// </summary>
    public static JournalStoragePropertiesUpdate Empty { get; } = new(set: null, remove: null);

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStoragePropertiesUpdate"/> class.
    /// </summary>
    /// <param name="set">Properties to set.</param>
    /// <param name="remove">Properties to remove.</param>
    public JournalStoragePropertiesUpdate(
        IReadOnlyDictionary<string, string>? set,
        IEnumerable<string>? remove)
    {
        Set = CopySet(set);
        Remove = CopyRemove(remove, Set);
    }

    /// <summary>
    /// Gets the caller-owned properties to set.
    /// </summary>
    public IReadOnlyDictionary<string, string> Set { get; }

    /// <summary>
    /// Gets the caller-owned properties to remove.
    /// </summary>
    public IReadOnlySet<string> Remove { get; }

    /// <summary>
    /// Gets a value indicating whether this update has no changes.
    /// </summary>
    public bool IsEmpty => Set.Count == 0 && Remove.Count == 0;

    /// <summary>
    /// Creates an update which sets one property.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <param name="value">The property value.</param>
    /// <returns>The update.</returns>
    public static JournalStoragePropertiesUpdate SetProperty(string propertyName, string value)
        => new(new Dictionary<string, string>(StringComparer.Ordinal) { [propertyName] = value }, remove: null);

    /// <summary>
    /// Creates an update which removes one property.
    /// </summary>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The update.</returns>
    public static JournalStoragePropertiesUpdate RemoveProperty(string propertyName)
        => new(set: null, remove: [propertyName]);

    private static ReadOnlyDictionary<string, string> CopySet(IReadOnlyDictionary<string, string>? set)
    {
        if (set is null || set.Count == 0)
        {
            return new(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var result = new Dictionary<string, string>(set.Count, StringComparer.Ordinal);
        foreach (var (key, value) in set)
        {
            JournalStorageProperties.ValidateCallerPropertyName(key);
            ArgumentNullException.ThrowIfNull(value);
            result.Add(key, value);
        }

        return new(result);
    }

    private static IReadOnlySet<string> CopyRemove(IEnumerable<string>? remove, IReadOnlyDictionary<string, string> set)
    {
        if (remove is null)
        {
            return Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in remove)
        {
            JournalStorageProperties.ValidateCallerPropertyName(key);
            if (set.ContainsKey(key))
            {
                throw new ArgumentException($"Journal storage property '{key}' cannot be both set and removed.", nameof(remove));
            }

            result.Add(key);
        }

        return result.ToFrozenSet(StringComparer.Ordinal);
    }
}
