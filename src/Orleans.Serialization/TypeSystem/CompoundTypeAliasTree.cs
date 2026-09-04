
using System;
using System.Collections.Generic;
using System.Threading;

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

    /// <summary>
    /// Merges the nodes of <paramref name="other"/> into this tree, keeping existing values (the
    /// first-one-wins semantics of <see cref="Add(Type, Type)"/>) and publishing copy-on-write so
    /// concurrent readers never observe a partially updated child collection.
    /// </summary>
    internal void MergeFrom(CompoundTypeAliasTree other)
    {
        if (other._children is not { Count: > 0 } otherChildren)
        {
            return;
        }

        var children = _children;
        Dictionary<object, CompoundTypeAliasTree>? updated = null;
        foreach (var pair in otherChildren)
        {
            if (children is not null && children.TryGetValue(pair.Key, out var existing))
            {
                if (existing.Value is null && pair.Value.Value is { } newValue)
                {
                    existing.Value = newValue;
                }

                existing.MergeFrom(pair.Value);
            }
            else
            {
                updated ??= children is null ? new() : new(children);
                updated[pair.Key] = pair.Value;
            }
        }

        if (updated is not null)
        {
            Volatile.Write(ref _children, updated);
        }
    }

    internal CompoundTypeAliasTree Clone()
    {
        var result = new CompoundTypeAliasTree(Key, Value);
        if (Volatile.Read(ref _children) is { } children)
        {
            var clonedChildren = new Dictionary<object, CompoundTypeAliasTree>(children.Count);
            foreach (var pair in children)
            {
                clonedChildren[pair.Key] = pair.Value.Clone();
            }

            result._children = clonedChildren;
        }

        return result;
    }

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
                // When the same grain interface is used across multiple assemblies which don't have cross references,
                // code-gen will generate code for both because it works in isolation, yet at startup they are combined.

                // In this case, if the key is present, and the value is the same as the one being added,
                // and due to them being logically the same, we can just return the existing CompoundTypeAliasTree.

                // The first one is allowed to win in this case.
                return existing;
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
