#nullable enable
using Orleans.Serialization.Invocation;
namespace Orleans.Runtime;

/// <summary>
/// Extension methods for <see cref="ITargetHolder"/>.
/// </summary>
public static class TargetHolderExtensions
{
    /// <summary>
    /// Gets the target with the specified type.
    /// </summary>
    /// <typeparam name="TTarget">The target type.</typeparam>
    /// <returns>The target.</returns>
    public static TTarget? GetTarget<TTarget>(this ITargetHolder targetHolder) where TTarget : class => targetHolder.GetTarget() as TTarget;

    /// <summary>
    /// Gets the component with the specified type.
    /// </summary>
    /// <typeparam name="TComponent">The component type.</typeparam>
    /// <returns>The component with the specified type.</returns>
    public static TComponent? GetComponent<TComponent>(this ITargetHolder targetHolder) where TComponent : class => targetHolder.GetComponent(typeof(TComponent)) as TComponent;
}