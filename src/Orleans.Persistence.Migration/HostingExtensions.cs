using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Persistence.Migration.Serialization;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Persistence.Migration
{
    public static class HostingExtensions
    {
        public static ISiloBuilder AddMigrationTools(this ISiloBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services
                    .AddSingleton<IPostConfigureOptions<OrleansJsonSerializerOptions>, ConfigureOrleansJsonSerializerOptions>()
                    .AddSingleton<OrleansMigrationJsonSerializer>()
                    .AddSingleton<IGrainTypeProvider, AttributeGrainTypeProvider>()
                    .AddSingleton<TypeResolver, CachedTypeResolver>()
                    .AddSingleton<TypeConverter>()
                    .AddSingleton<GrainTypeResolver>()
                    .AddSingleton<GrainInterfaceTypeResolver>()
                    .AddSingleton<IGrainReferenceExtractor, GrainReferenceExtractor>();
            });
            return builder;
        }
    }
}
