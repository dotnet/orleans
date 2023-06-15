#nullable enable

using System;
using System.Collections.Generic;

namespace Orleans.Serialization.TypeSystem;

/// <summary>
/// Represents a compound type aliases as a prefix tree.
/// </summary>
public class CompoundTypeAliasTree
{
    private Dictionary<object, CompoundTypeAliasTree>? _children;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompoundTypeAliasTree"/> class.
    /// </summary>
    private CompoundTypeAliasTree(object? key, Type? value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>
    /// Gets the key for this node.
    /// </summary>
    public object? Key { get; }

    /// <summary>
    /// Gets the value for this node.
    /// </summary>
    public Type? Value { get; private set; }

    /// <summary>
    /// Creates a new tree with a root node which has no key or value.
    /// </summary>
    public static CompoundTypeAliasTree Create() => new(default, default);

    internal CompoundTypeAliasTree? GetChildOrDefault(object key)
    {
        TryGetChild(key, out var result);
        return result;
    }

    internal bool TryGetChild(object key, out CompoundTypeAliasTree? result)
    {
        if (_children is { } children)
        {
            return children.TryGetValue(key, out result);
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Adds a node to the tree.
    /// </summary>
    /// <param name="key">The key for the new node.</param>
    public CompoundTypeAliasTree Add(Type key) => AddInternal(key);

    /// <summary>
    /// Adds a node to the tree.
    /// </summary>
    /// <param name="key">The key for the new node.</param>
    public CompoundTypeAliasTree Add(string key) => AddInternal(key);

    /// <summary>
    /// Adds a node to the tree.
    /// </summary>
    /// <param name="key">The key for the new node.</param>
    /// <param name="value">The value for the new node.</param>
    public CompoundTypeAliasTree Add(string key, Type value) => AddInternal(key, value);

    /// <summary>
    /// Adds a node to the tree.
    /// </summary>
    /// <param name="key">The key for the new node.</param>
    /// <param name="value">The value for the new node.</param>
    public CompoundTypeAliasTree Add(Type key, Type value) => AddInternal(key, value);

    private CompoundTypeAliasTree AddInternal(object key) => AddInternal(key, default);
    private CompoundTypeAliasTree AddInternal(object key, Type? value)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(key, nameof(key));
#else
        if (key is null) throw new ArgumentNullException(nameof(key));
#endif
        _children ??= new();

        if (_children.TryGetValue(key, out var existing))
        {
            if (value is not null && existing.Value is { } type && type != value)
            {
                throw new ArgumentException("A key with this value already exists");
            }

            existing.Value = value;
            return existing;
        }
        else
        {
            return _children[key] = new CompoundTypeAliasTree(key, value);
        }
    }
}