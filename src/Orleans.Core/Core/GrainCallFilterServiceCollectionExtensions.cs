using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class GrainCallFilterServiceCollectionExtensions
    {
        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        [Obsolete("Use ISiloBuilder." + nameof(AddIncomingGrainCallFilter), error: true)]
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection services, IIncomingGrainCallFilter filter)
        {
            throw new NotSupportedException($"{nameof(AddGrainCallFilter)} is no longer supported. Use ISiloBuilder.AddIncomingGrainCallFilter(...) instead.");
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        [Obsolete("Use ISiloBuilder." + nameof(AddIncomingGrainCallFilter), error: true)]
        public static IServiceCollection AddGrainCallFilter<TImplementation>(this IServiceCollection services)
            where TImplementation : class, IIncomingGrainCallFilter
        {
            throw new NotSupportedException($"{nameof(AddGrainCallFilter)} is no longer supported. Use ISiloBuilder.AddIncomingGrainCallFilter(...) instead.");
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        [Obsolete("Use ISiloBuilder." + nameof(AddIncomingGrainCallFilter), error: true)]
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection services, GrainCallFilterDelegate filter)
        {
            throw new NotSupportedException($"{nameof(AddGrainCallFilter)} is no longer supported. Use ISiloBuilder.AddIncomingGrainCallFilter(...) instead.");
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddIncomingGrainCallFilter(this IServiceCollection services, IIncomingGrainCallFilter filter)
        {
            return services.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddIncomingGrainCallFilter<TImplementation>(this IServiceCollection services)
            where TImplementation : class, IIncomingGrainCallFilter
        {
            return services.AddSingleton<IIncomingGrainCallFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddIncomingGrainCallFilter(this IServiceCollection services, IncomingGrainCallFilterDelegate filter)
        {
            return services.AddSingleton<IIncomingGrainCallFilter>(new IncomingGrainCallFilterWrapper(filter));
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddOutgoingGrainCallFilter(this IServiceCollection services, IOutgoingGrainCallFilter filter)
        {
            return services.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddOutgoingGrainCallFilter<TImplementation>(this IServiceCollection services)
            where TImplementation : class, IOutgoingGrainCallFilter
        {
            return services.AddSingleton<IOutgoingGrainCallFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddOutgoingGrainCallFilter(this IServiceCollection services, OutgoingGrainCallFilterDelegate filter)
        {
            return services.AddSingleton<IOutgoingGrainCallFilter>(new OutgoingGrainCallFilterWrapper(filter));
        }

        /// <summary>
        /// Adapts <see cref="OutgoingGrainCallFilterDelegate"/> delegates to the <see cref="IOutgoingGrainCallFilter"/> interface.
        /// </summary>
        private class OutgoingGrainCallFilterWrapper : IOutgoingGrainCallFilter
        {
            private readonly OutgoingGrainCallFilterDelegate interceptor;

            /// <summary>
            /// Initializes a new instance of the <see cref="OutgoingGrainCallFilterWrapper"/> class.
            /// </summary>
            /// <param name="interceptor">
            /// The interceptor.
            /// </param>
            public OutgoingGrainCallFilterWrapper(OutgoingGrainCallFilterDelegate interceptor)
            {
                this.interceptor = interceptor;
            }

            /// <inheritdoc />
            public Task Invoke(IOutgoingGrainCallContext context) => this.interceptor.Invoke(context);
        }

        /// <summary>
        /// Implements <see cref="IIncomingGrainCallFilter"/> by delegating all <see cref="IIncomingGrainCallFilter.Invoke"/> calls to the provided delegate.
        /// </summary>
        private class IncomingGrainCallFilterWrapper : IIncomingGrainCallFilter
        {
            private readonly IncomingGrainCallFilterDelegate interceptor;

            /// <summary>
            /// Initializes a new instance of the <see cref="IncomingGrainCallFilterWrapper"/> class.
            /// </summary>
            /// <param name="interceptor">
            /// The interceptor.
            /// </param>
            public IncomingGrainCallFilterWrapper(IncomingGrainCallFilterDelegate interceptor)
            {
                this.interceptor = interceptor;
            }

            /// <summary>
            /// Invokes this filter.
            /// </summary>
            /// <param name="context">The grain call context.</param>
            /// <returns>A <see cref="Task"/> representing the work performed.</returns>
            public Task Invoke(IIncomingGrainCallContext context) => this.interceptor.Invoke(context);
        }
    }
}
