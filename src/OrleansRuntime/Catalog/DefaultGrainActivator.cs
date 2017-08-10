using System;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime
{
    /// <summary>
    /// <see cref="IGrainActivator"/> that uses type activation to create grains.
    /// </summary>
    public class DefaultGrainActivator : IGrainActivator
    {
        private readonly GrainConstructorFacetArguementsFactory arguementsFactory;
        private readonly ConcurrentDictionary<Type, ObjectFactory> typeActivatorCache;

        /// <summary>
        /// Public constructor
        /// </summary>
        /// <param name="service"></param>
        public DefaultGrainActivator(IServiceProvider service)
        {
            this.arguementsFactory = new GrainConstructorFacetArguementsFactory(service);
            this.typeActivatorCache = new ConcurrentDictionary<Type, ObjectFactory>();
        }

    /// <inheritdoc />
    public virtual object Create(IGrainActivationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var grainType = context.GrainType;

            if (grainType == null)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture, "The '{0}' property of '{1}' must not be null.",
                        nameof(context.GrainType),
                        nameof(IGrainActivationContext)));
            }

            var serviceProvider = context.ActivationServices;
            var activator = this.typeActivatorCache.GetOrAdd(grainType, this.CreateFactory);
            var grain = activator(serviceProvider, this.arguementsFactory.CreateArguements(context));
            return grain;
        }

        /// <inheritdoc />
        public virtual void Release(IGrainActivationContext context, object grain)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (grain == null)
            {
                throw new ArgumentNullException(nameof(grain));
            }

            var disposable = grain as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }

        private ObjectFactory CreateFactory(Type type)
        {
            return ActivatorUtilities.CreateFactory(type, this.arguementsFactory.ArguementTypes(type));
        }
    }
}
