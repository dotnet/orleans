#nullable enable
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Represents an object which can be invoked asynchronously.
    /// </summary>
    public interface IInvokable : IDisposable
    {
        /// <summary>
        /// Gets the invocation target.
        /// </summary>
        /// <typeparam name="TTarget">The target type.</typeparam>
        /// <returns>The invocation target.</returns>
        TTarget? GetTarget<TTarget>();

        /// <summary>
        /// Sets the invocation target from an instance of <see cref="ITargetHolder"/>.
        /// </summary>
        /// <typeparam name="TTargetHolder">The target holder type.</typeparam>
        /// <param name="holder">The invocation target.</param>
        void SetTarget<TTargetHolder>(TTargetHolder holder) where TTargetHolder : ITargetHolder;

        /// <summary>
        /// Invoke the object.
        /// </summary>
        ValueTask<Response> Invoke();

        /// <summary>
        /// Gets the number of arguments.
        /// </summary>
        int ArgumentCount { get; }

        /// <summary>
        /// Gets the argument at the specified index.
        /// </summary>
        /// <typeparam name="TArgument">The argument type.</typeparam>
        /// <param name="index">The argument index.</param>
        /// <returns>The argument at the specified index.</returns>
        TArgument? GetArgument<TArgument>(int index);

        /// <summary>
        /// Sets the argument at the specified index.
        /// </summary>
        /// <typeparam name="TArgument">The argument type.</typeparam>
        /// <param name="index">The argument index.</param>
        /// <param name="value">The argument value</param>
        void SetArgument<TArgument>(int index, in TArgument value);

        /// <summary>
        /// Gets the method name.
        /// </summary>
        string MethodName { get; }

        /// <summary>
        /// Gets the full interface name.
        /// </summary>
        string InterfaceName { get; }

        /// <summary>
        /// Gets the method info object, which may be <see langword="null"/>.
        /// </summary>
        MethodInfo Method { get; }

        /// <summary>
        /// Gets the interface type.
        /// </summary>
        Type InterfaceType { get; }

        /// <summary>
        /// Gets the type arguments for the method if the method is generic, otherwise an empty array.
        /// </summary>
        Type[] MethodTypeArguments { get; }

        /// <summary>
        /// Gets the type arguments for the interface if the interface is generic, otherwise an empty array.
        /// </summary>
        Type[] InterfaceTypeArguments { get; }

        /// <summary>
        /// Gets the parameter types for the method.
        /// </summary>
        Type[] ParameterTypes { get; }
    }
}