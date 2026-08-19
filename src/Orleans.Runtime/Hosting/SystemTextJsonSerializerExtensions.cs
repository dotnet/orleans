using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

#nullable enable

namespace Orleans.Hosting
{
    public static class SystemTextJsonSerializerExtensions
    {
        /// <summary>
        /// Replaces <see cref="Newtonsoft.Json.JsonSerializer" /> with <see cref="System.Text.Json.JsonSerializer" /> as the default grain storage serializer.
        /// </summary>
        /// <param name="siloBuilder">The silo builder to configure with System.Text.Json grain storage support.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder UseSystemTextJsonGrainStorageSerializer(this ISiloBuilder siloBuilder)
        {
            siloBuilder.Services.AddSingleton<IGrainStorageSerializer, SystemTextJsonGrainStorageSerializer>();
            return siloBuilder;
        }
    }
}
