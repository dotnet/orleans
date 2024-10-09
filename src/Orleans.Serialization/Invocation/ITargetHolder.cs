#nullable enable
using System;

namespace Orleans.Serialization.Invocation;

/// <summary>
/// Represents an object which holds an invocation target as well as target extensions.
/// </summary>
public interface ITargetHolder
{
    /// <summary>
    /// Gets the target instance.
    /// </summary>
    /// <returns>The target.</returns>
    object? GetTarget();

    /// <summary>
    /// Gets the component with the specified type.
    /// </summary>
    /// <param name="componentType">The component type.</param>
    /// <returns>The component with the specified type.</returns>
    object? GetComponent(Type componentType);
}
