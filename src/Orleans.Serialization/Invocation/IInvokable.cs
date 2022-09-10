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
        /// <returns>The invocation target.</returns>
        object? GetTarget();

        /// <summary>
        /// Sets the invocation target from an instance of <see cref="ITargetHolder"/>.
        /// </summary>
        /// <param name="holder">The invocation target.</param>
        void SetTarget(ITargetHolder holder);

        /// <summary>
        /// Invoke the object.
        /// </summary>
        ValueTask<Response> Invoke();

        /// <summary>
        /// Gets the number of arguments.
        /// </summary>
        int GetArgumentCount();

        /// <summary>
        /// Gets the argument at the specified index.
        /// </summary>
        /// <param name="index">The argument index.</param>
        /// <returns>The argument at the specified index.</returns>
        object? GetArgument(int index);

        /// <summary>
        /// Sets the argument at the specified index.
        /// </summary>
        /// <param name="index">The argument index.</param>
        /// <param name="value">The argument value</param>
        void SetArgument(int index, object value);

        /// <summary>
        /// Gets the method name.
        /// </summary>
        string GetMethodName();

        /// <summary>
        /// Gets the full interface name.
        /// </summary>
        string GetInterfaceName();

        /// <summary>
        /// Gets the activity name, which refers to both the interface name and method name.
        /// </summary>
        string GetActivityName();

        /// <summary>
        /// Gets the method info object, which may be <see langword="null"/>.
        /// </summary>
        MethodInfo GetMethod();

        /// <summary>
        /// Gets the interface type.
        /// </summary>
        Type GetInterfaceType();
    }
}