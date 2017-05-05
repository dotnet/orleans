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
        private readonly Func<Type, ObjectFactory> createFactory = type => ActivatorUtilities.CreateFactory(type, Type.EmptyTypes);
        private readonly ConcurrentDictionary<Type, ObjectFactory> typeActivatorCache = new ConcurrentDictionary<Type, ObjectFactory>();

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
            var activator = this.typeActivatorCache.GetOrAdd(grainType, this.createFactory);
            var grain = activator(serviceProvider, arguments: null);
            return grain;
        }
    }
}
