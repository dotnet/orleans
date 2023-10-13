using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Storage.Internal;

namespace Orleans.Storage
{

    /// <summary>
    /// This is a simple in-memory grain implementation of a storage provider.
    /// </summary>
    /// <remarks>
    /// This storage provider is ONLY intended for simple in-memory Development / Unit Test scenarios.
    /// This class should NOT be used in Production environment, 
    ///  because [by-design] it does not provide any resilience 
    ///  or long-term persistence capabilities.
    /// </remarks>
    /// <example>
    /// Example configuration for this storage provider in OrleansConfiguration.xml file:
    /// <code>
    /// &lt;OrleansConfiguration xmlns="urn:orleans">
    ///   &lt;Globals>
    ///     &lt;StorageProviders>
    ///       &lt;Provider Type="Orleans.Storage.MemoryStorage" Name="MemoryStore" />
    ///   &lt;/StorageProviders>
    /// </code>
    /// </example>
    [DebuggerDisplay("MemoryStore:{" + nameof(name) + "}")]
    public class MemoryGrainStorage : IGrainStorage, IDisposable
    {
        private Lazy<IMemoryStorageGrain>[] storageGrains;
        private readonly ILogger logger;
        private readonly IGrainStorageSerializer storageSerializer;

        /// <summary> Name of this storage provider instance. </summary>
        private readonly string name;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryGrainStorage"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="options">The options.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="defaultGrainStorageSerializer">The default grain storage serializer.</param>
        public MemoryGrainStorage(string name, MemoryGrainStorageOptions options, ILogger<MemoryGrainStorage> logger, IGrainFactory grainFactory, IGrainStorageSerializer defaultGrainStorageSerializer)
        {
            this.name = name;
            this.logger = logger;
            this.storageSerializer = options.GrainStorageSerializer ?? defaultGrainStorageSerializer;

            //Init
            logger.LogInformation("Init: Name={Name} NumStorageGrains={NumStorageGrains}", name, options.NumStorageGrains);

            storageGrains = new Lazy<IMemoryStorageGrain>[options.NumStorageGrains];
            for (int i = 0; i < storageGrains.Length; i++)
            {
                int idx = i; // Capture variable to avoid modified closure error
                storageGrains[idx] = new Lazy<IMemoryStorageGrain>(() => grainFactory.GetGrain<IMemoryStorageGrain>(idx));
            }
        }

        /// <inheritdoc/>
        public virtual async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var key = MakeKey(grainType, grainId);

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Read Keys={Keys}", key);

            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            var state = await storageGrain.ReadStateAsync<ReadOnlyMemory<byte>>(key);
            if (state != null)
            {
                var loadedState = ConvertFromStorageFormat<T>(state.State);
                grainState.ETag = state.ETag;
                grainState.State = loadedState ?? Activator.CreateInstance<T>();
                grainState.RecordExists = true;
            }
        }

        /// <inheritdoc/>
        public virtual async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var key = MakeKey(grainType, grainId);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Write Keys={Keys} Data={Data} Etag={Etag}", key, grainState.State, grainState.ETag);
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                var data = ConvertToStorageFormat<T>(grainState.State);
                var binaryGrainState = new GrainState<ReadOnlyMemory<byte>>(data, grainState.ETag)
                {
                    RecordExists = grainState.RecordExists
                };
                grainState.ETag = await storageGrain.WriteStateAsync(key, binaryGrainState);
                grainState.RecordExists = true;
            }
            catch (MemoryStorageEtagMismatchException e)
            {
                throw e.AsInconsistentStateException();
            }
        }

        /// <inheritdoc/>
        public virtual async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var key = MakeKey(grainType, grainId);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Delete Keys={Keys} Etag={Etag}", key, grainState.ETag);
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                await storageGrain.DeleteStateAsync<ReadOnlyMemory<byte>>(key, grainState.ETag);
                grainState.ETag = null;
                grainState.RecordExists = false;
            }
            catch (MemoryStorageEtagMismatchException e)
            {
                throw e.AsInconsistentStateException();
            }
        }

        private static string MakeKey(string grainType, GrainId grainId) => $"{grainType}/{grainId}";

        private IMemoryStorageGrain GetStorageGrain(string id)
        {
            var idx = (uint)id.GetHashCode() % (uint)storageGrains.Length;
            return storageGrains[idx].Value;
        }

        /// <inheritdoc/>
        public void Dispose() => storageGrains = null;

        /// <summary>
        /// Deserialize from binary data
        /// </summary>
        /// <param name="data">The serialized stored data</param>
        internal T ConvertFromStorageFormat<T>(ReadOnlyMemory<byte> data)
        {

            T dataValue = default;
            try
            {
                dataValue = this.storageSerializer.Deserialize<T>(data);
            }
            catch (Exception exc)
            {
                var sb = new StringBuilder();
                if (data.ToArray().Length > 0)
                {
                    sb.AppendFormat("Unable to convert from storage format GrainStateEntity.Data={0}", data);
                }

                if (dataValue != null)
                {
                    sb.AppendFormat("Data Value={0} Type={1}", dataValue, dataValue.GetType());
                }

                logger.LogError(exc, "{Message}", sb.ToString());
                throw new AggregateException(sb.ToString(), exc);
            }

            return dataValue;
        }

        /// <summary>
        /// Serialize to the storage format.
        /// </summary>
        /// <param name="grainState">The grain state data to be serialized</param>
        /// <remarks>
        /// See:
        /// http://msdn.microsoft.com/en-us/library/system.web.script.serialization.javascriptserializer.aspx
        /// for more on the JSON serializer.
        /// </remarks>
        internal ReadOnlyMemory<byte> ConvertToStorageFormat<T>(T grainState)
        {
            // Convert to binary format
            return this.storageSerializer.Serialize<T>(grainState);
        }
    }

    /// <summary>
    /// Factory for creating MemoryGrainStorage
    /// </summary>
    public static class MemoryGrainStorageFactory
    {
        /// <summary>
        /// Creates a new <see cref="MemoryGrainStorage"/> instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The name.</param>
        /// <returns>The storage.</returns>
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<MemoryGrainStorage>(services,
                services.GetRequiredService<IOptionsMonitor<MemoryGrainStorageOptions>>().Get(name), name);
        }
    }
}
