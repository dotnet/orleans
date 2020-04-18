using System;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Responsible for resolving an <see cref="IPlacementDirector"/> for a <see cref="PlacementStrategy"/>.
    /// </summary>
    public sealed class PlacementDirectorResolver
    {
        private readonly ImmutableDictionary<Type, IPlacementDirector> _directors;

        public PlacementDirectorResolver(IServiceProvider services)
        {
            _directors = GetAllDirectors(services);

            static ImmutableDictionary<Type, IPlacementDirector> GetAllDirectors(IServiceProvider services)
            {
                var directors = services.GetRequiredService<IKeyedServiceCollection<Type, IPlacementDirector>>();
                var builder = ImmutableDictionary.CreateBuilder<Type, IPlacementDirector>();
                foreach (var service in directors.GetServices(services))
                {
                    builder[service.Key] = service.GetService(services);
                }

                return builder.ToImmutable();
            }
        }

        public IPlacementDirector GetPlacementDirector(PlacementStrategy placementStrategy) => _directors[placementStrategy.GetType()];
    }
}
