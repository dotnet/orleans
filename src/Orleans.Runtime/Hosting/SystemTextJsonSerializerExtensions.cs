using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Providers.StorageSerializer;
using Orleans.Serialization;
using Orleans.Storage;

#nullable enable

namespace Orleans.Runtime.Hosting
{
    public static class SystemTextJsonSerializerExtensions
    {
        /// <summary>
        /// Replaces <see cref="Newtonsoft.Json.JsonSerializer" /> with <see cref="System.Text.Json.JsonSerializer" /> as the default grain storage serializer
        /// </summary>
        /// <param name="siloBuilder">The siloBuilder to configure with System.Text.Json grain storage support</param>
        /// <returns></returns>
        [Experimental("ORLEANSEXP006")]
        public static ISiloBuilder UseSystemTextJsonGrainStorageSerializer(this ISiloBuilder siloBuilder)
        {
            siloBuilder.Services.AddSingleton<IGrainStorageSerializer, SystemTextJsonGrainStorageSerializer>();
            return siloBuilder;
        }
    }
}
