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
        private readonly Type _grainClass;

        /// <summary>
        /// Initializes a new <see cref="DefaultGrainActivator"/> instance.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="grainClass">The grain class.</param>
        public DefaultGrainActivator(IServiceProvider serviceProvider, Type grainClass)
        {
            _argumentFactory = new GrainConstructorArgumentFactory(serviceProvider, grainClass);
            _grainInstanceFactory = ActivatorUtilities.CreateFactory(grainClass, _argumentFactory.ArgumentTypes);
            _grainClass = grainClass;
        }

        /// <inheritdoc/>
        public object CreateInstance(IGrainContext context)
        {
            try
            {
                var args = _argumentFactory.CreateArguments(context);
                return _grainInstanceFactory(context.ActivationServices, args);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Failed to create an instance of grain type '{_grainClass}'. See {nameof(Exception.InnerException)} for details.",
                    exception);
            }
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