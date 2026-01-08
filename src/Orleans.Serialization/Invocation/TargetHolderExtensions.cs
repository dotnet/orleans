#nullable enable
using Orleans.Serialization.Invocation;
namespace Orleans.Runtime;

/// <summary>
/// Extension methods for <see cref="ITargetHolder"/>.
/// </summary>
public static class TargetHolderExtensions
{
    /// <summary>
    /// Gets the component with the specified type.
    /// </summary>
    /// <typeparam name="TComponent">The component type.</typeparam>
    /// <param name="targetHolder">The target holder from which to retrieve the component.</param>
    /// <returns>The component with the specified type.</returns>
    public static TComponent? GetComponent<TComponent>(this ITargetHolder targetHolder) where TComponent : class => targetHolder.GetComponent(typeof(TComponent)) as TComponent;
}
