using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Utilities
{
    /// <summary>
    /// Utility methods for creating factories which construct instances of objects using an <see cref="IServiceProvider"/>.
    /// </summary>
    internal static class FactoryUtility
    {
        private static readonly object[] EmptyArguments = new object[0];

        /// <summary>
        /// Creates a factory returning a new <typeparamref name="TInstance"/>.
        /// </summary>
        /// <typeparam name="TInstance">The instance type.</typeparam>
        /// <param name="serviceProvider">The service provider.</param>
        /// <returns>A new factory.</returns>
        public static Factory<TInstance> Create<TInstance>(IServiceProvider serviceProvider)
        {
            var factory = ActivatorUtilities.CreateFactory(typeof(TInstance), Type.EmptyTypes);
            return () => (TInstance)factory(serviceProvider, EmptyArguments);
        }

        /// <summary>
        /// Creates a factory returning a new <typeparamref name="TInstance"/> given an argument of type <typeparamref name="TParam1"/>.
        /// </summary>
        /// <typeparam name="TParam1">The type of the parameter to the factory.</typeparam>
        /// <typeparam name="TInstance">The instance type.</typeparam>
        /// <param name="serviceProvider">The service provider.</param>
        /// <returns>A new factory.</returns>
        public static Factory<TParam1, TInstance> Create<TParam1, TInstance>(IServiceProvider serviceProvider)
        {
            var factory = ActivatorUtilities.CreateFactory(typeof(TInstance), new[] { typeof(TParam1) });
            return arg1 => (TInstance)factory(serviceProvider, new object[] { arg1 });
        }

        /// <summary>
        /// Creates a factory returning a new <typeparamref name="TInstance"/> given arguments of the specified types.
        /// </summary>
        /// <typeparam name="TParam1">The type of the 1st parameter to the factory.</typeparam>
        /// <typeparam name="TParam2">The type of the 2nd parameter to the factory.</typeparam>
        /// <typeparam name="TInstance">The instance type.</typeparam>
        /// <param name="serviceProvider">The service provider.</param>
        /// <returns>A new factory.</returns>
        public static Factory<TParam1, TParam2, TInstance> Create<TParam1, TParam2, TInstance>(IServiceProvider serviceProvider)
        {
            var factory = ActivatorUtilities.CreateFactory(typeof(TInstance), new[] { typeof(TParam1), typeof(TParam2) });
            return (arg1, arg2) => (TInstance)factory(serviceProvider, new object[] { arg1, arg2 });
        }
        /// <summary>
        /// Creates a factory returning a new <typeparamref name="TInstance"/> given arguments of the specified types.
        /// </summary>
        /// <typeparam name="TParam1">The type of the 1st parameter to the factory.</typeparam>
        /// <typeparam name="TParam2">The type of the 2nd parameter to the factory.</typeparam>
        /// <typeparam name="TParam3">The type of the 3rd parameter to the factory.</typeparam>
        /// <typeparam name="TInstance">The instance type.</typeparam>
        /// <param name="serviceProvider">The service provider.</param>
        /// <returns>A new factory.</returns>
        public static Factory<TParam1, TParam2, TParam3, TInstance> Create<TParam1, TParam2, TParam3, TInstance>(IServiceProvider serviceProvider)
        {
            var factory = ActivatorUtilities.CreateFactory(typeof(TInstance), new[] { typeof(TParam1), typeof(TParam2), typeof(TParam3) });
            return (arg1, arg2, arg3) => (TInstance)factory(serviceProvider, new object[] { arg1, arg2, arg3 });
        }
    }
}
