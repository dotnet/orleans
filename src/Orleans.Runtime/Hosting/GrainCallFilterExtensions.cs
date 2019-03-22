using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    public static class GrainCallFilterExtensions
    {

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="siloBuilder">The silo builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter(this ISiloBuilder siloBuilder, IIncomingGrainCallFilter filter)
        {
            return siloBuilder.ConfigureServices(services => services.AddSingleton(filter));
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="siloBuilder">The silo builder.</param>
        /// <returns>The service collection.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter<TImplementation>(this ISiloBuilder siloBuilder)
            where TImplementation : class, IIncomingGrainCallFilter
        {
            return siloBuilder.ConfigureServices(services =>
                services.AddSingleton<IIncomingGrainCallFilter, TImplementation>());
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="siloBuilder">The silo builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter(this ISiloBuilder siloBuilder, IncomingGrainCallFilterDelegate filter)
        {
            return siloBuilder.ConfigureServices(services =>
                services.AddSingleton<IIncomingGrainCallFilter>(new IncomingGrainCallFilterWrapper(filter)));
        }

        private class IncomingGrainCallFilterWrapper : IIncomingGrainCallFilter
        {
            private readonly IncomingGrainCallFilterDelegate interceptor;

            public IncomingGrainCallFilterWrapper(IncomingGrainCallFilterDelegate interceptor)
            {
                this.interceptor = interceptor;
            }

            public Task Invoke(IIncomingGrainCallContext context) => this.interceptor.Invoke(context);
        }
    }
}
