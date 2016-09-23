using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.SqlUtils;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Orleans.Storage
{
    /// <summary>
    /// Logging codes used by <see cref="AdoNetStorageProvider"/>.
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
    public class AdoNetStorageProvider: IStorageProvider
    {
        /// <summary>
        /// The Service ID for which this relational provider is used.
        /// </summary>
        private string ServiceId { get; set; }

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
        public const string DefaultInitializationQuery = "SELECT QueryKey, QueryText FROM OrleansQuery WHERE QueryKey = 'WriteToStorageKey' OR QueryKey = 'ReadFromStorageKey' OR QueryKey = 'ClearStorageKey';";

        /// <summary>
        /// The default ADO.NET invariant used for storage if none is given. This corresponds to Orleans.Runtime.Constants.INVARIANT_NAME_SQL_SERVER.
        /// </summary>
        public const string DefaultAdoInvariantInvariantPropertyName = AdoNetInvariants.InvariantNameSqlServer;

        /// <summary>
        /// A slot key for storage string payload formatted in JSON.
        /// </summary>
        public const string UseJsonFormatPropertyName = "UseJsonFormat";

        /// <summary>
        /// A slot key for storage string payload formatted in XML.
        /// </summary>
        public const string UseXmlFormatPropertyName = "UseXmlFormat";

        /// <summary>
        /// A slot key for storage binary payload. The format can be native Orleans binary format, Bond or something user configured.
        /// </summary>
        public const string UseBinaryFormatPropertyName = "UseBinaryFormat";

        /// <summary>
        /// The canonical constant for storage provider connection string.
        /// </summary>
        /// <remarks>Corresponds to Orleans.Runtime.Constants.DATA_CONNECTION_STRING_NAME.</remarks>
        public const string DataConnectionStringPropertyName = "DataConnectionString";

        /// <summary>
        /// The canonical constant for relational storage provider connection string.
        /// </summary>
        /// <remarks>Corresponds to Orleans.Runtime.Constants.ADO_INVARIANT_NAME.</remarks>
        public const string DataConnectionInvariantPropertyName = "AdoInvariant";

        /// <summary>
        /// The queries currently used. When this is updated, the new queries will take effect immediately.
        /// </summary>
        public RelationalStorageProviderQueries CurrentOperationalQueries { get; set; }

        /// <summary>
        /// A strategy to pick a serializer or a deserializer for storage operations. This can be used to:
        /// 1) Add a custom serializer or deserializer for use in storage provider operations.
        /// 2) In combination with serializer or deserializer to update stored object version.
        /// 3) Per-grain storage format selection
        /// 4) Switch storage format first by reading using the save format and then writing in the new format.
        /// </summary>
        public IStorageSerializationPicker StorageSerializationPicker { get; set; }

        /// <summary>
        /// The hash generator used to hash natural keys, grain ID and grain type to a more narrow index.
        /// </summary>
        public IStorageHasherPicker HashPicker { get; set; } = new StorageHasherPicker(new[] { new OrleansDefaultHasher() });

        /// <summary> Logger used by this storage provider instance. </summary>
        /// <see cref="IStorageProvider.Log"/>
        public Logger Log { get; private set; }

        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider.Name"/>
        public string Name { get; private set; }

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider.Init"/>
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            if(string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("The parameter must contain characters", nameof(name));
            }

            if(providerRuntime == null)
            {
                throw new ArgumentNullException(nameof(providerRuntime));
            }

            if(config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if(!config.Properties.ContainsKey(DataConnectionStringPropertyName))
            {
                throw new BadProviderConfigException($"The {DataConnectionStringPropertyName} setting has not been configured. Add a {DataConnectionStringPropertyName} setting with a valid connection string.");
            }

            //NOTE: StorageSerializationPicker should be defined outside and given as a parameter in constructor or via Init in IProviderConfiguration perhaps.
            //Currently this limits one's options to much to the current situation of providing only one serializer for serialization and deserialization
            //with no regard to state update or serializer changes. Maybe have this serialized as a JSON in props and read via a key?
            StorageSerializationPicker = new DefaultRelationalStoragePicker(ConfigureDeserializers(config), ConfigureSerializers(config));

            //NOTE: Currently there should be only one pair of providers given. That is, only UseJsonFormatPropertyName, UseXmlFormatPropertyName or UseBinaryFormatPropertyName.
            if(StorageSerializationPicker.Deserializers.Count > 1 || StorageSerializationPicker.Serializers.Count > 1)
            {
                throw new ArgumentException("Configuration error, only one serializer and deserializer should be given.", nameof(config));
            }

            if(StorageSerializationPicker.Deserializers.Count == 0 || StorageSerializationPicker.Serializers.Count == 0)
            {
                StorageSerializationPicker.Deserializers.Add(new OrleansStorageDefaultBinaryDeserializer(UseBinaryFormatPropertyName));
                StorageSerializationPicker.Serializers.Add(new OrleansStorageDefaultBinarySerializer(UseBinaryFormatPropertyName));
            }

            var connectionInvariant = config.Properties.ContainsKey(DataConnectionInvariantPropertyName) ? config.Properties[DataConnectionInvariantPropertyName] : DefaultAdoInvariantInvariantPropertyName;
            Storage = RelationalStorage.CreateInstance(connectionInvariant, config.Properties[DataConnectionStringPropertyName]);
            ServiceId = providerRuntime.ServiceId.ToString();

            var queries = await Storage.ReadAsync(DefaultInitializationQuery, command => { }, (selector, resultSetCount, token) =>
            {
                return Task.FromResult(Tuple.Create(selector.GetValue<string>("QueryKey"), selector.GetValue<string>("QueryText")));
            }).ConfigureAwait(false);

            CurrentOperationalQueries = new RelationalStorageProviderQueries(
                queries.Single(i => i.Item1 == "WriteToStorageKey").Item2,
                queries.Single(i => i.Item1 == "ReadFromStorageKey").Item2,
                queries.Single(i => i.Item1 == "ClearStorageKey").Item2);

            Log = providerRuntime.GetLogger(GetType().FullName);
            Name = name;

            Log.Info((int)RelationalStorageProviderCodes.RelationalProviderInitProvider, $"Initialized storage provider: ServiceId={ServiceId} ProviderName={Name} Invariant={Storage.InvariantName} ConnectionString={Storage.ConnectionString}.");
        }


        /// <summary>
        /// <see cref="IProvider.Close"/>
        /// </summary>
        public Task Close()
        {
            return TaskDone.Done;
        }


        /// <summary>Clear state data function for this storage provider.</summary>
        /// <see cref="IStorageProvider.ClearStateAsync(string, GrainReference, IGrainState)"/>.
        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown,
            //even if not as clear as when using explicitly checked parameters.
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var baseGrainType = ExtractBaseClass(grainType);
            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderClearing, LogString("Clearing grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString()));
            }

            string storageVersion = null;
            try
            {
                var clearRecord = (await Storage.ReadAsync(CurrentOperationalQueries.ClearState, command =>
                {
                    command.AddParameter("GrainIdHash", HashPicker.PickHasher(ServiceId, Name, baseGrainType, grainReference, grainState).Hash(grainId.GetHashBytes()));
                    command.AddParameter("GrainIdN0", grainId.N0Key);
                    command.AddParameter("GrainIdN1", grainId.N1Key);
                    command.AddParameter("GrainTypeHash", HashPicker.PickHasher(ServiceId, Name, baseGrainType, grainReference, grainState).Hash(Encoding.UTF8.GetBytes(baseGrainType)));
                    command.AddParameter("GrainTypeString", baseGrainType);
                    command.AddParameter("GrainIdExtensionString", grainId.StringKey);
                    command.AddParameter("ServiceId", ServiceId);
                    command.AddParameter("GrainStateVersion", !string.IsNullOrWhiteSpace(grainState.ETag) ? int.Parse(grainState.ETag, CultureInfo.InvariantCulture) : default(int?));
                }, (selector, resultSetCount, token) => { return Task.FromResult(selector.GetValue(0).ToString()); }, CancellationToken.None).ConfigureAwait(false));
                storageVersion = clearRecord.SingleOrDefault();
            }
            catch(Exception ex)
            {
                Log.Error((int)RelationalStorageProviderCodes.RelationalProviderDeleteError, LogString("Error clearing grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString(), ex.Message), ex);
                throw;
            }

            const string OperationString = "ClearState";
            var inconsistentStateException = CheckVersionInconsistency(OperationString, ServiceId, Name, storageVersion, grainState.ETag, baseGrainType, grainId.ToString());
            if(inconsistentStateException != null)
            {
                throw inconsistentStateException;
            }

            //No errors found, the version of the state held by the grain can be updated and also the state.
            grainState.ETag = storageVersion;
            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderCleared, LogString("Cleared grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString()));
            }
        }


        /// <summary> Read state data function for this storage provider.</summary>
        /// <see cref="IStorageProvider.ReadStateAsync(string, GrainReference, IGrainState)"/>.
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var baseGrainType = ExtractBaseClass(grainType);
            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderReading, LogString("Reading grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString()));
            }

            try
            {
                SerializationChoice choice = StorageSerializationPicker.PickDeserializer(ServiceId, Name, baseGrainType, grainReference, grainState, null);
                if(choice.Deserializer == null)
                {
                    var errorString = LogString("No deserializer found", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString());
                    Log.Error((int)RelationalStorageProviderCodes.RelationalProviderNoDeserializer, errorString);
                    throw new InvalidOperationException(errorString);
                }

                var commandBehavior = choice.PreferStreaming ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
                var grainStateType = grainState.State.GetType();
                var readRecords = (await Storage.ReadAsync(CurrentOperationalQueries.ReadFromStorage, (command =>
                {
                    command.AddParameter("GrainIdHash", HashPicker.PickHasher(ServiceId, Name, baseGrainType, grainReference, grainState).Hash(grainId.GetHashBytes()));
                    command.AddParameter("GrainIdN0", grainId.N0Key);
                    command.AddParameter("GrainIdN1", grainId.N1Key);
                    command.AddParameter("GrainTypeHash", HashPicker.PickHasher(ServiceId, Name, baseGrainType, grainReference, grainState).Hash(Encoding.UTF8.GetBytes(baseGrainType)));
                    command.AddParameter("GrainTypeString", baseGrainType);
                    command.AddParameter("GrainIdExtensionString", grainId.StringKey);
                    command.AddParameter("ServiceId", ServiceId);
                }), async (selector, resultSetCount, token) =>
                {
                    object storageState = null;
                    int? version;
                    if(choice.PreferStreaming)
                    {
                        //When streaming via ADO.NET, using CommandBehavior.SequentialAccess, the order of
                        //the columns on how they are read needs to be exactly this.
                        const int binaryColumnPositionInSelect = 0;
                        const int xmlColumnPositionInSelect = 1;
                        const int jsonColumnPositionInSelect = 2;
                        var streamSelector = (DbDataReader)selector;
                        if(!(await streamSelector.IsDBNullAsync(binaryColumnPositionInSelect)))
                        {
                            using(var downloadStream = streamSelector.GetStream(binaryColumnPositionInSelect, Storage))
                            {
                                storageState = choice.Deserializer.Deserialize(downloadStream, grainStateType);
                            }
                        }

                        if(!(await streamSelector.IsDBNullAsync(xmlColumnPositionInSelect)))
                        {
                            using(var downloadStream = streamSelector.GetTextReader(xmlColumnPositionInSelect))
                            {
                                storageState = choice.Deserializer.Deserialize(downloadStream, grainStateType);
                            }
                        }

                        if(!(await streamSelector.IsDBNullAsync(jsonColumnPositionInSelect)))
                        {
                            using(var downloadStream = streamSelector.GetTextReader(jsonColumnPositionInSelect))
                            {
                                storageState = choice.Deserializer.Deserialize(downloadStream, grainStateType);
                            }
                        }

                        version = await streamSelector.GetValueAsync<int?>("Version");
                    }
                    else
                    {
                        //All but one of these should be null. All will be read and an appropriate deserializer picked.
                        //NOTE: When streaming will be implemented, it is worthwhile to optimize this so that the defined
                        //serializer will be picked and then streaming tried according to its tag.
                        object payload;
                        payload = selector.GetValueOrDefault<byte[]>("PayloadBinary");
                        if(payload == null)
                        {
                            payload = selector.GetValueOrDefault<string>("PayloadXml");
                        }

                        if(payload == null)
                        {
                            payload = selector.GetValueOrDefault<string>("PayloadJson");
                        }

                        if(payload != null)
                        {
                            storageState = choice.Deserializer.Deserialize(payload, grainStateType);
                        }

                        version = selector.GetValue<int?>("Version");
                    }

                    return Tuple.Create(storageState, version?.ToString(CultureInfo.InvariantCulture));
                }, CancellationToken.None, commandBehavior).ConfigureAwait(false)).SingleOrDefault();

                object state = readRecords != null ? readRecords.Item1 : null;
                string etag = readRecords != null ? readRecords.Item2 : null;
                if(state == null)
                {
                    Log.Info((int)RelationalStorageProviderCodes.RelationalProviderNoStateFound, LogString("Null grain state read (default will be instantiated)", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString()));
                    state = Activator.CreateInstance(grainStateType);
                }

                grainState.State = state;
                grainState.ETag = etag;
                if(Log.IsVerbose3)
                {
                    Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderRead, LogString("Read grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString()));
                }
            }
            catch(Exception ex)
            {
                Log.Error((int)RelationalStorageProviderCodes.RelationalProviderReadError, LogString("Error reading grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString(), ex.Message), ex);
                throw;
            }
        }


        /// <summary> Write state data function for this storage provider.</summary>
        /// <see cref="IStorageProvider.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var data = grainState.State;
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var baseGrainType = ExtractBaseClass(grainType);
            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderWriting, LogString("Writing grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString()));
            }

            string storageVersion = null;
            try
            {

                var writeRecord = await Storage.ReadAsync(CurrentOperationalQueries.WriteToStorage, command =>
                {
                    command.AddParameter("GrainIdHash", HashPicker.PickHasher(ServiceId, Name, baseGrainType, grainReference, grainState).Hash(grainId.GetHashBytes()));
                    command.AddParameter("GrainIdN0", grainId.N0Key);
                    command.AddParameter("GrainIdN1", grainId.N1Key);
                    command.AddParameter("GrainTypeHash", HashPicker.PickHasher(ServiceId, Name, baseGrainType, grainReference, grainState).Hash(Encoding.UTF8.GetBytes(baseGrainType)));
                    command.AddParameter("GrainTypeString", (string)baseGrainType);
                    command.AddParameter("GrainIdExtensionString", grainId.StringKey);
                    command.AddParameter("ServiceId", ServiceId);
                    command.AddParameter("GrainStateVersion", !string.IsNullOrWhiteSpace(grainState.ETag) ? int.Parse(grainState.ETag, CultureInfo.InvariantCulture) : default(int?));

                    SerializationChoice serializer = StorageSerializationPicker.PickSerializer(ServiceId, Name, (string)baseGrainType, grainReference, grainState);
                    command.AddParameter("PayloadBinary", (byte[])(serializer.Serializer.Tag == UseBinaryFormatPropertyName ? serializer.Serializer.Serialize(data) : null));
                    command.AddParameter("PayloadJson", (string)(serializer.Serializer.Tag == UseJsonFormatPropertyName ? serializer.Serializer.Serialize(data) : null));
                    command.AddParameter("PayloadXml", (string)(serializer.Serializer.Tag == UseXmlFormatPropertyName ? serializer.Serializer.Serialize(data) : null));
                }, (selector, resultSetCount, token) =>
                { return Task.FromResult(selector.GetValueOrDefault<int?>("NewGrainStateVersion").ToString()); }, CancellationToken.None).ConfigureAwait(false);
                storageVersion = writeRecord.SingleOrDefault();
            }
            catch(Exception ex)
            {
                Log.Error((int)RelationalStorageProviderCodes.RelationalProviderWriteError, LogString("Error writing grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString(), ex.Message), ex);
                throw;
            }

            const string OperationString = "WriteState";
            var inconsistentStateException = CheckVersionInconsistency(OperationString, ServiceId, Name, storageVersion, grainState.ETag, baseGrainType, grainId.ToString());
            if(inconsistentStateException != null)
            {
                throw inconsistentStateException;
            }

            //No errors found, the version of the state held by the grain can be updated.
            grainState.ETag = storageVersion;

            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderWrote, LogString("Wrote grain state", ServiceId, Name, grainState.ETag, baseGrainType, grainId.ToString()));
            }
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
        /// Writes a consistent log message from the given parameters.
        /// </summary>
        /// <param name="operationProlog">A free form prolog information to log.</param>
        /// <param name="serviceId">Service Id.</param>
        /// <param name="providerName">The name of this storage provider.</param>
        /// <param name="version">The grain version.</param>
        /// <param name="normalizedGrainType">Grain type without generics information.</param>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="exceptionMessage">An optional exception message information to log.</param>
        /// <returns>A log string to be printed.</returns>
        private string LogString(string operationProlog, string serviceId, string providerName, string version, string normalizedGrainType, string grainId, string exceptionMessage = null)
        {
            const string Exception = " Exception=";
            return $"{operationProlog}: ServiceId={serviceId} ProviderName={providerName} GrainType={normalizedGrainType} GrainId={grainId} ETag={version}{(exceptionMessage != null ? Exception + exceptionMessage : string.Empty)}.";
        }


        /// <summary>
        /// Extracts a grain ID as a string and appends the key extension with '#' infix is present.
        /// </summary>
        /// <param name="grainReference">The reference from which to extract the ID.</param>
        /// <returns>The grain ID as a string.</returns>
        /// <remarks>This likely should exist in Orleans core in more optimized form.</remarks>
        private static AdoGrainKey GrainIdAndExtensionAsString(GrainReference grainReference)
        {
            //Kudos for https://github.com/tsibelman for the algorithm. See more at https://github.com/dotnet/orleans/issues/1905.
            string keyExtension;
            AdoGrainKey key;
            if(grainReference.IsPrimaryKeyBasedOnLong())
            {
                key = new AdoGrainKey(grainReference.GetPrimaryKeyLong(out keyExtension), keyExtension);
            }
            else
            {
                key = new AdoGrainKey(grainReference.GetPrimaryKey(out keyExtension), keyExtension);
            }

            return key;
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
            if(genericPosition != -1)
            {
                //The following relies the generic argument list to be in form as described
                //at https://msdn.microsoft.com/en-us/library/w3f99sx1.aspx.
                var split = typeName.Split(BaseClassExtractionSplitDelimeters, StringSplitOptions.RemoveEmptyEntries);
                return split[0] + string.Format($"[{string.Join(",", split.Skip(1).Where(i => i.Length > 1 && i[0] != ',').Select(i => string.Format($"[{i.Substring(0, i.IndexOf(',', i.IndexOf(',') + 1))}]")))}]");
            }

            return typeName;
        }


        private static ICollection<IStorageDeserializer> ConfigureDeserializers(IProviderConfiguration config)
        {
            const string @true = "true";
            var deserializers = new List<IStorageDeserializer>();
            if(config.Properties.ContainsKey(UseJsonFormatPropertyName) && @true.Equals(config.Properties[UseJsonFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                var jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(), config);
                deserializers.Add(new OrleansStorageDefaultJsonDeserializer(jsonSettings, UseJsonFormatPropertyName));
            }

            if(config.Properties.ContainsKey(UseXmlFormatPropertyName) && @true.Equals(config.Properties[UseXmlFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                deserializers.Add(new OrleansStorageDefaultXmlDeserializer(UseXmlFormatPropertyName));
            }

            if(config.Properties.ContainsKey(UseBinaryFormatPropertyName) && @true.Equals(config.Properties[UseBinaryFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                deserializers.Add(new OrleansStorageDefaultBinaryDeserializer(UseBinaryFormatPropertyName));
            }

            return deserializers;
        }


        private static ICollection<IStorageSerializer> ConfigureSerializers(IProviderConfiguration config)
        {
            const string @true = "true";
            var serializers = new List<IStorageSerializer>();
            if(config.Properties.ContainsKey(UseJsonFormatPropertyName) && @true.Equals(config.Properties[UseJsonFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                var jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(), config);
                serializers.Add(new OrleansStorageDefaultJsonSerializer(jsonSettings, UseJsonFormatPropertyName));
            }

            if(config.Properties.ContainsKey(UseXmlFormatPropertyName) && @true.Equals(config.Properties[UseXmlFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                serializers.Add(new OrleansStorageDefaultXmlSerializer(UseXmlFormatPropertyName));
            }

            if(config.Properties.ContainsKey(UseBinaryFormatPropertyName) && @true.Equals(config.Properties[UseBinaryFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                serializers.Add(new OrleansStorageDefaultBinarySerializer(UseBinaryFormatPropertyName));
            }

            return serializers;
        }
    }
}