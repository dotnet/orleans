using Orleans.Persistence.AdoNet.Storage;
using Orleans.Providers;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Overrides;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;

namespace Orleans.Storage
{
    /// <summary>
    /// Logging codes used by <see cref="AdoNetGrainStorage"/>.
    /// </summary>
    /// <remarks> These are taken from <em>Orleans.Providers.ProviderErrorCode</em> and <em>Orleans.Providers.AzureProviderErrorCode</em>.</remarks>
    internal enum RelationalStorageProviderCodes
    {
        //These is from Orleans.Providers.ProviderErrorCode and Orleans.Providers.AzureProviderErrorCode.
        ProvidersBase = 200000,

        RelationalProviderBase = ProvidersBase + 400,
        RelationalProviderDeleteError = RelationalProviderBase + 8,
        RelationalProviderInitProvider = RelationalProviderBase + 9,
        RelationalProviderNoDeserializer = RelationalProviderBase + 10,
        RelationalProviderNoStateFound = RelationalProviderBase + 11,
        RelationalProviderClearing = RelationalProviderBase + 12,
        RelationalProviderCleared = RelationalProviderBase + 13,
        RelationalProviderReading = RelationalProviderBase + 14,
        RelationalProviderRead = RelationalProviderBase + 15,
        RelationalProviderReadError = RelationalProviderBase + 16,
        RelationalProviderWriting = RelationalProviderBase + 17,
        RelationalProviderWrote = RelationalProviderBase + 18,
        RelationalProviderWriteError = RelationalProviderBase + 19
    }

    public static class AdoNetGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<AdoNetGrainStorageOptions>>();
            var clusterOptions = services.GetProviderClusterOptions(name);
            return ActivatorUtilities.CreateInstance<AdoNetGrainStorage>(services, Options.Create(optionsMonitor.Get(name)), name, clusterOptions);
        }
    }

    /// <summary>
    /// A storage provider for writing grain state data to relational storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required configuration params: <c>DataConnectionString</c>
    /// </para>
    /// <para>
    /// Optional configuration params:
    /// <c>AdoInvariant</c> -- defaults to <c>System.Data.SqlClient</c>
    /// <c>UseJsonFormat</c> -- defaults to <c>false</c>
    /// <c>UseXmlFormat</c> -- defaults to <c>false</c>
    /// <c>UseBinaryFormat</c> -- defaults to <c>true</c>
    /// </para>
    /// </remarks>
    [DebuggerDisplay("Name = {Name}, ConnectionString = {Storage.ConnectionString}")]
    public class AdoNetGrainStorage: IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        public IGrainStorageSerializer Serializer { get; set; }

        /// <summary>
        /// Tag for BinaryFormatSerializer
        /// </summary>
        public const string BinaryFormatSerializerTag = "BinaryFormatSerializer";
        /// <summary>
        /// Tag for JsonFormatSerializer
        /// </summary>
        public const string JsonFormatSerializerTag = "JsonFormatSerializer";
        /// <summary>
        /// Tag for XmlFormatSerializer
        /// </summary>
        public const string XmlFormatSerializerTag = "XmlFormatSerializer";

        /// <summary>
        /// The Service ID for which this relational provider is used.
        /// </summary>
        private readonly string serviceId;

        private readonly ILogger logger;

        /// <summary>
        /// The storage used for back-end operations.
        /// </summary>
        private IRelationalStorage Storage { get; set; }

        /// <summary>
        /// These chars are delimiters when used to extract a class base type from a class
        /// that is either <see cref="Type.AssemblyQualifiedName"/> or <see cref="Type.FullName"/>.
        /// <see cref="ExtractBaseClass(string)"/>.
        /// </summary>
        private static char[] BaseClassExtractionSplitDelimeters { get; } = new[] { '[', ']' };

        /// <summary>
        /// The default query to initialize this structure from the Orleans database.
        /// </summary>
        public const string DefaultInitializationQuery = "SELECT QueryKey, QueryText FROM OrleansQuery WHERE QueryKey = 'WriteToStorageKey' OR QueryKey = 'ReadFromStorageKey' OR QueryKey = 'ClearStorageKey'";

        /// <summary>
        /// The queries currently used. When this is updated, the new queries will take effect immediately.
        /// </summary>
        public RelationalStorageProviderQueries CurrentOperationalQueries { get; set; }

        /// <summary>
        /// The hash generator used to hash natural keys, grain ID and grain type to a more narrow index.
        /// </summary>
        public IStorageHasherPicker HashPicker { get; set; } = new StorageHasherPicker(new[] { new OrleansDefaultHasher() });

        private readonly AdoNetGrainStorageOptions options;
        private readonly IProviderRuntime providerRuntime;
        private readonly string name;

        public AdoNetGrainStorage(
            ILogger<AdoNetGrainStorage> logger,
            IProviderRuntime providerRuntime,
            IOptions<AdoNetGrainStorageOptions> options,
            IOptions<ClusterOptions> clusterOptions,
            string name)
        {
            this.options = options.Value;
            this.providerRuntime = providerRuntime;
            this.name = name;
            this.logger = logger;
            this.serviceId = clusterOptions.Value.ServiceId;
            this.Serializer = options.Value.GrainStorageSerializer;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<AdoNetGrainStorage>(this.name), this.options.InitStage, Init, Close);
        }
        /// <summary>Clear state data function for this storage provider.</summary>
        /// <see cref="IGrainStorage.ClearStateAsync{T}"/>.
        public async Task ClearStateAsync<T>(string grainType, GrainId grainReference, IGrainState<T> grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown,
            //even if not as clear as when using explicitly checked parameters.
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var baseGrainType = ExtractBaseClass(grainType);
            if(logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    (int)RelationalStorageProviderCodes.RelationalProviderClearing,
                    "Clearing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                    serviceId,
                    name,
                    baseGrainType,
                    grainId,
                    grainState.ETag);
            }

            string storageVersion = null;
            try
            {
                var grainIdHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(grainId.GetHashBytes());
                var grainTypeHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(Encoding.UTF8.GetBytes(baseGrainType));
                var clearRecord = (await Storage.ReadAsync(CurrentOperationalQueries.ClearState, command =>
                {
                    command.AddParameter("GrainIdHash", grainIdHash);
                    command.AddParameter("GrainIdN0", grainId.N0Key);
                    command.AddParameter("GrainIdN1", grainId.N1Key);
                    command.AddParameter("GrainTypeHash", grainTypeHash);
                    command.AddParameter("GrainTypeString", baseGrainType);
                    command.AddParameter("GrainIdExtensionString", grainId.StringKey);
                    command.AddParameter("ServiceId", serviceId);
                    command.AddParameter("GrainStateVersion", !string.IsNullOrWhiteSpace(grainState.ETag) ? int.Parse(grainState.ETag, CultureInfo.InvariantCulture) : default(int?));
                }, (selector, resultSetCount, token) => Task.FromResult(selector.GetValue(0).ToString()), cancellationToken: CancellationToken.None).ConfigureAwait(false));
                storageVersion = clearRecord.SingleOrDefault();
            }
            catch(Exception ex)
            {
                logger.LogError(
                    (int)RelationalStorageProviderCodes.RelationalProviderDeleteError,
                    ex,
                    "Error clearing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                    serviceId,
                    name,
                    baseGrainType,
                    grainId,
                    grainState.ETag);
                throw;
            }

            const string OperationString = "ClearState";
            var inconsistentStateException = CheckVersionInconsistency(OperationString, serviceId, this.name, storageVersion, grainState.ETag, baseGrainType, grainId.ToString());
            if(inconsistentStateException != null)
            {
                throw inconsistentStateException;
            }

            //No errors found, the version of the state held by the grain can be updated and also the state.
            grainState.ETag = storageVersion;
            grainState.RecordExists = false;
            if(logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    (int)RelationalStorageProviderCodes.RelationalProviderCleared,
                    "Cleared grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                    serviceId,
                    name,
                    baseGrainType,
                    grainId,
                    grainState.ETag);
            }
        }


        /// <summary> Read state data function for this storage provider.</summary>
        /// <see cref="IGrainStorage.ReadStateAsync{T}"/>.
        public async Task ReadStateAsync<T>(string grainType, GrainId grainReference, IGrainState<T> grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var baseGrainType = ExtractBaseClass(grainType);
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    (int)RelationalStorageProviderCodes.RelationalProviderReading,
                    "Reading grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                    serviceId,
                    name,
                    baseGrainType,
                    grainId,
                    grainState.ETag);
            }

            try
            {
                var commandBehavior = CommandBehavior.Default;
                var grainIdHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(grainId.GetHashBytes());
                var grainTypeHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(Encoding.UTF8.GetBytes(baseGrainType));
                var readRecords = (await Storage.ReadAsync(
                    CurrentOperationalQueries.ReadFromStorage,
                    command =>
                    {
                        command.AddParameter("GrainIdHash", grainIdHash);
                        command.AddParameter("GrainIdN0", grainId.N0Key);
                        command.AddParameter("GrainIdN1", grainId.N1Key);
                        command.AddParameter("GrainTypeHash", grainTypeHash);
                        command.AddParameter("GrainTypeString", baseGrainType);
                        command.AddParameter("GrainIdExtensionString", grainId.StringKey);
                        command.AddParameter("ServiceId", serviceId);
                    },
                    (selector, resultSetCount, token) =>
                    {
                        object storageState = null;
                        int? version;
                        byte[] payload;
                        payload = selector.GetValueOrDefault<byte[]>("PayloadBinary");
                        if (payload != null)
                        {
                            storageState = Serializer.Deserialize<T>(new BinaryData(payload));
                        }
                        version = selector.GetNullableInt32("Version");
                        var result = Tuple.Create(storageState, version?.ToString(CultureInfo.InvariantCulture));
                        return Task.FromResult(result);
                    },
                    commandBehavior, CancellationToken.None).ConfigureAwait(false)).SingleOrDefault();

                T state = readRecords != null ? (T) readRecords.Item1 : default;
                string etag = readRecords != null ? readRecords.Item2 : null;
                bool recordExists = readRecords != null;
                if(state == null)
                {
                    logger.LogInformation(
                        (int)RelationalStorageProviderCodes.RelationalProviderNoStateFound,
                        "Null grain state read (default will be instantiated): ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                        serviceId,
                        name,
                        baseGrainType,
                        grainId,
                        grainState.ETag);
                    state = Activator.CreateInstance<T>();
                }

                grainState.State = state;
                grainState.ETag = etag;
                grainState.RecordExists = recordExists;
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace(
                        (int)RelationalStorageProviderCodes.RelationalProviderRead,
                        "Read grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                        serviceId,
                        name,
                        baseGrainType,
                        grainId,
                        grainState.ETag);
                }
            }
            catch(Exception ex)
            {
                logger.LogError(
                    (int)RelationalStorageProviderCodes.RelationalProviderReadError,
                    ex,
                    "Error reading grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                    serviceId,
                    name,
                    baseGrainType,
                    grainId,
                    grainState.ETag);
                throw;
            }
        }


        /// <summary> Write state data function for this storage provider.</summary>
        /// <see cref="IGrainStorage.WriteStateAsync{T}"/>
        public async Task WriteStateAsync<T>(string grainType, GrainId grainReference, IGrainState<T> grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var data = grainState.State;
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var baseGrainType = ExtractBaseClass(grainType);
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    (int)RelationalStorageProviderCodes.RelationalProviderWriting,
                    "Writing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                    serviceId,
                    name,
                    baseGrainType,
                    grainId,
                    grainState.ETag);
            }

            string storageVersion = null;
            try
            {
                var grainIdHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(grainId.GetHashBytes());
                var grainTypeHash = HashPicker.PickHasher(serviceId, this.name, baseGrainType, grainReference, grainState).Hash(Encoding.UTF8.GetBytes(baseGrainType));
                var writeRecord = await Storage.ReadAsync(CurrentOperationalQueries.WriteToStorage, command =>
                {
                    var serialized = this.Serializer.Serialize<T>(grainState.State);

                    command.AddParameter("GrainIdHash", grainIdHash);
                    command.AddParameter("GrainIdN0", grainId.N0Key);
                    command.AddParameter("GrainIdN1", grainId.N1Key);
                    command.AddParameter("GrainTypeHash", grainTypeHash);
                    command.AddParameter("GrainTypeString", baseGrainType);
                    command.AddParameter("GrainIdExtensionString", grainId.StringKey);
                    command.AddParameter("ServiceId", serviceId);
                    command.AddParameter("GrainStateVersion", !string.IsNullOrWhiteSpace(grainState.ETag) ? int.Parse(grainState.ETag, CultureInfo.InvariantCulture) : default(int?));
                    command.AddParameter("PayloadBinary", serialized.ToArray());
                }, (selector, resultSetCount, token) =>
                { return Task.FromResult(selector.GetNullableInt32("NewGrainStateVersion").ToString()); }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                storageVersion = writeRecord.SingleOrDefault();
            }
            catch(Exception ex)
            {
                logger.LogError(
                    (int)RelationalStorageProviderCodes.RelationalProviderWriteError,
                    ex,
                    "Error writing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                    serviceId,
                    name,
                    baseGrainType,
                    grainId,
                    grainState.ETag);
                throw;
            }

            const string OperationString = "WriteState";
            var inconsistentStateException = CheckVersionInconsistency(OperationString, serviceId, this.name, storageVersion, grainState.ETag, baseGrainType, grainId.ToString());
            if(inconsistentStateException != null)
            {
                throw inconsistentStateException;
            }

            //No errors found, the version of the state held by the grain can be updated.
            grainState.ETag = storageVersion;
            grainState.RecordExists = true;

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    (int)RelationalStorageProviderCodes.RelationalProviderWrote,
                    "Wrote grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={BaseGrainType} GrainId={GrainId} ETag={ETag}.",
                    serviceId,
                    name,
                    baseGrainType,
                    grainId,
                    grainState.ETag);
            }
        }

        /// <summary> Initialization function for this storage provider. </summary>
        private async Task Init(CancellationToken cancellationToken)
        {
            Storage = RelationalStorage.CreateInstance(options.Invariant, options.ConnectionString);
            var queries = await Storage.ReadAsync(DefaultInitializationQuery, command => { }, (selector, resultSetCount, token) =>
            {
                return Task.FromResult(Tuple.Create(selector.GetValue<string>("QueryKey"), selector.GetValue<string>("QueryText")));
            }).ConfigureAwait(false);

            CurrentOperationalQueries = new RelationalStorageProviderQueries(
                queries.Single(i => i.Item1 == "WriteToStorageKey").Item2,
                queries.Single(i => i.Item1 == "ReadFromStorageKey").Item2,
                queries.Single(i => i.Item1 == "ClearStorageKey").Item2);

            logger.LogInformation(
                (int)RelationalStorageProviderCodes.RelationalProviderInitProvider,
                "Initialized storage provider: ServiceId={ServiceId} ProviderName={Name} Invariant={InvariantName} ConnectionString={ConnectionString}.",
                serviceId,
                name,
                Storage.InvariantName,
                ConfigUtilities.RedactConnectionStringInfo(Storage.ConnectionString));
        }


        /// <summary>
        /// Close this provider
        /// </summary>
        private Task Close(CancellationToken token)
        {
            return Task.CompletedTask;
        }


        /// <summary>
        /// Checks for version inconsistency as defined in the database scripts.
        /// </summary>
        /// <param name="serviceId">Service Id.</param>
        /// <param name="providerName">The name of this storage provider.</param>
        /// <param name="operation">The operation attempted.</param>
        /// <param name="storageVersion">The version from storage.</param>
        /// <param name="grainVersion">The grain version.</param>
        /// <param name="normalizedGrainType">Grain type without generics information.</param>
        /// <param name="grainId">The grain ID.</param>
        /// <returns>An exception for throwing or <em>null</em> if no violation was detected.</returns>
        /// <remarks>This means that the version was not updated in the database or the version storage version was something else than null
        /// when the grain version was null, meaning effectively a double activation and save.</remarks>
        private static InconsistentStateException CheckVersionInconsistency(string operation, string serviceId, string providerName, string storageVersion, string grainVersion, string normalizedGrainType, string grainId)
        {
            //If these are the same, it means no row was inserted or updated in the storage.
            //Effectively it means the UPDATE or INSERT conditions failed due to ETag violation.
            //Also if grainState.ETag storageVersion is null and storage comes back as null,
            //it means two grains were activated an the other one succeeded in writing its state.
            //
            //NOTE: the storage could return also the new and old ETag (Version), but currently it doesn't.
            if(storageVersion == grainVersion || storageVersion == string.Empty)
            {
                //TODO: Note that this error message should be canonical across back-ends.
                return new InconsistentStateException($"Version conflict ({operation}): ServiceId={serviceId} ProviderName={providerName} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainVersion}.");
            }

            return null;
        }

        /// <summary>
        /// Extracts a grain ID as a string and appends the key extension with '#' infix is present.
        /// </summary>
        /// <returns>The grain ID as a string.</returns>
        /// <remarks>This likely should exist in Orleans core in more optimized form.</remarks>
        private static AdoGrainKey GrainIdAndExtensionAsString(GrainId grainId)
        {
            string keyExt;
            if (grainId.TryGetGuidKey(out var guid, out keyExt))
            {
                return new AdoGrainKey(guid, keyExt);
            }

            if (grainId.TryGetIntegerKey(out var integer, out keyExt))
            {
                return new AdoGrainKey(integer, keyExt);
            }

            return new AdoGrainKey(grainId.Key.ToString());
        }

        /// <summary>
        /// Extracts a base class from a string that is either <see cref="Type.AssemblyQualifiedName"/> or
        /// <see cref="Type.FullName"/> or returns the one given as a parameter if no type is given.
        /// </summary>
        /// <param name="typeName">The base class name to give.</param>
        /// <returns>The extracted base class or the one given as a parameter if it didn't have a generic part.</returns>
        private static string ExtractBaseClass(string typeName)
        {
            var genericPosition = typeName.IndexOf("`", StringComparison.OrdinalIgnoreCase);
            if (genericPosition != -1)
            {
                //The following relies the generic argument list to be in form as described
                //at https://msdn.microsoft.com/en-us/library/w3f99sx1.aspx.
                var split = typeName.Split(BaseClassExtractionSplitDelimeters, StringSplitOptions.RemoveEmptyEntries);
                var stripped = new Queue<string>(split.Where(i => i.Length > 1 && i[0] != ',').Select(WithoutAssemblyVersion));

                return ReformatClassName(stripped);
            }

            return typeName;

            string WithoutAssemblyVersion(string input)
            {
                var asmNameIndex = input.IndexOf(',');
                if (asmNameIndex >= 0)
                {
                    var asmVersionIndex = input.IndexOf(',', asmNameIndex + 1);
                    if (asmVersionIndex >= 0) return input[..asmVersionIndex];
                    return input[..asmNameIndex];
                }

                return input;
            }

            string ReformatClassName(Queue<string> segments)
            {
                var simpleTypeName = segments.Dequeue();
                var arity = GetGenericArity(simpleTypeName);
                if (arity <= 0) return simpleTypeName;

                var args = new List<string>(arity);
                for (var i = 0; i < arity; i++)
                {
                    args.Add(ReformatClassName(segments));
                }

                return $"{simpleTypeName}[{string.Join(",", args.Select(arg => $"[{arg}]"))}]";
            }

            int GetGenericArity(string input)
            {
                var arityIndex = input.IndexOf("`", StringComparison.OrdinalIgnoreCase);
                if (arityIndex != -1)
                {
                    return int.Parse(input.AsSpan()[(arityIndex + 1)..]);
                }

                return 0;
            }
        }
    }
}