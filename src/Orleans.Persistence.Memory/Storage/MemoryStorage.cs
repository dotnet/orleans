#nullable enable
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
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
    [DebuggerDisplay("MemoryStore:{" + nameof(name) + "}")]
    public partial class MemoryGrainStorage : IGrainStorage, IDisposable
    {
        private Lazy<IMemoryStorageGrain>[] storageGrains;
        private readonly ILogger logger;
        private readonly IActivatorProvider _activatorProvider;
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
        public MemoryGrainStorage(
            string name,
            MemoryGrainStorageOptions options,
            ILogger<MemoryGrainStorage> logger,
            IGrainFactory grainFactory,
            IGrainStorageSerializer defaultGrainStorageSerializer,
            IActivatorProvider activatorProvider)
        {
            this.name = name;
            this.logger = logger;
            _activatorProvider = activatorProvider;
            this.storageSerializer = options.GrainStorageSerializer ?? defaultGrainStorageSerializer;

            LogDebugInit(name, options.NumStorageGrains);
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

            LogTraceRead(key);
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            var state = await storageGrain.ReadStateAsync<ReadOnlyMemory<byte>>(key);
            if (state != null)
            {
                var loadedState = ConvertFromStorageFormat<T>(state.State);
                grainState.ETag = state.ETag;
                grainState.State = loadedState ?? CreateInstance<T>();
                grainState.RecordExists = loadedState != null;
            }
            else
            {
                grainState.ETag = null;
                grainState.State = CreateInstance<T>();
                grainState.RecordExists = false;
            }
        }

        /// <inheritdoc/>
        public virtual async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var key = MakeKey(grainType, grainId);
            LogTraceWrite(key, grainState.State!, grainState.ETag);
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
            LogTraceDelete(key, grainState.ETag);
            IMemoryStorageGrain storageGrain = GetStorageGrain(key);
            try
            {
                await storageGrain.DeleteStateAsync<ReadOnlyMemory<byte>>(key, grainState.ETag);
                grainState.ETag = null;
                grainState.RecordExists = false;
                grainState.State = CreateInstance<T>();
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
        public void Dispose() { }

        /// <summary>
        /// Deserialize from binary data
        /// </summary>
        /// <param name="data">The serialized stored data</param>
        internal T? ConvertFromStorageFormat<T>(ReadOnlyMemory<byte> data)
        {
            T? dataValue = default;
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
                LogError(sb, exc);
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

        private T CreateInstance<T>() => _activatorProvider.GetActivator<T>().Create();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Init: Name={Name} NumStorageGrains={NumStorageGrains}"
        )]
        private partial void LogDebugInit(string name, int numStorageGrains);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Read Keys={Keys}"
        )]
        private partial void LogTraceRead(string keys);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Write Keys={Keys} Data={Data} Etag={Etag}"
        )]
        private partial void LogTraceWrite(string keys, object data, string etag);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Delete Keys={Keys} Etag={Etag}"
        )]
        private partial void LogTraceDelete(string keys, string etag);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "{Message}"
        )]
        private partial void LogError(StringBuilder message, Exception exception);
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
        public static MemoryGrainStorage Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<MemoryGrainStorage>(services,
                services.GetRequiredService<IOptionsMonitor<MemoryGrainStorageOptions>>().Get(name), name);
        }
    }
}
