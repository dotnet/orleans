using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime
{
    /// <summary>
    /// The default <see cref="IGrainActivator"/> implementation.
    /// </summary>
    public class DefaultGrainActivator : IGrainActivator
    {
        private readonly ObjectFactory _grainInstanceFactory;
        private readonly GrainConstructorArgumentFactory _argumentFactory;

        /// <summary>
        /// Initializes a new <see cref="DefaultGrainActivator"/> instance.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="grainClass">The grain class.</param>
        public DefaultGrainActivator(IServiceProvider serviceProvider, Type grainClass)
        {
            _argumentFactory = new GrainConstructorArgumentFactory(serviceProvider, grainClass);
            _grainInstanceFactory = ActivatorUtilities.CreateFactory(grainClass, _argumentFactory.ArgumentTypes);
        }

        /// <inheritdoc/>
        public object CreateInstance(IGrainContext context)
        {
            var args = _argumentFactory.CreateArguments(context);
            return _grainInstanceFactory(context.ActivationServices, args);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeInstance(IGrainContext context, object instance)
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}