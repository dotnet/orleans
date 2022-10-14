using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.TypeSystem;
using Orleans.Storage;
using Samples.StorageProviders;
using TestExtensions;
using UnitTests.Persistence;
using UnitTests.StorageTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Persistence
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestCategory("Persistence")]
    public class PersistenceProviderTests_Local
    {
        private readonly IProviderRuntime providerRuntime;
        private readonly Dictionary<string, string> providerCfgProps = new Dictionary<string, string>();
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;

        public PersistenceProviderTests_Local(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.providerRuntime = new ClientProviderRuntime(
                fixture.InternalGrainFactory,
                fixture.Services,
                fixture.Services.GetRequiredService<ClientGrainContext>());
            this.providerCfgProps.Clear();
        }

        [Fact, TestCategory("Functional")]
        public async Task PersistenceProvider_Mock_WriteRead()
        {
            const string testName = nameof(PersistenceProvider_Mock_WriteRead);

            var store = ActivatorUtilities.CreateInstance<MockStorageProvider>(fixture.Services, testName);

            await Test_PersistenceProvider_WriteRead(testName, store);
        }

        [Fact, TestCategory("Functional")]
        public async Task PersistenceProvider_FileStore_WriteRead()
        {
            const string testName = nameof(PersistenceProvider_FileStore_WriteRead);

            var store = new OrleansFileStorage("Data");
            await Test_PersistenceProvider_WriteRead(testName, store);
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("AzureStorage")]
        public async Task PersistenceProvider_Azure_Read()
        {
            TestUtils.CheckForAzureStorage();
            const string testName = nameof(PersistenceProvider_Azure_Read);

            AzureTableGrainStorage store = await InitAzureTableGrainStorage();
            await Test_PersistenceProvider_Read(testName, store);
        }

        [SkippableTheory, TestCategory("Functional"), TestCategory("AzureStorage")]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData(15 * 64 * 1024 - 256, false)]
        [InlineData(15 * 32 * 1024 - 256, true)]
        public async Task PersistenceProvider_Azure_WriteRead(int? stringLength, bool useJson)
        {
            var testName = string.Format("{0}({1} = {2}, {3} = {4})",
                nameof(PersistenceProvider_Azure_WriteRead),
                nameof(stringLength), stringLength == null ? "default" : stringLength.ToString(),
                nameof(useJson), useJson);

            var grainState = TestStoreGrainState.NewRandomState(stringLength);
            EnsureEnvironmentSupportsState(grainState);

            var store = await InitAzureTableGrainStorage(useJson);

            await Test_PersistenceProvider_WriteRead(testName, store, grainState);
        }

        [SkippableTheory, TestCategory("Functional"), TestCategory("AzureStorage")]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData(15 * 64 * 1024 - 256, false)]
        [InlineData(15 * 32 * 1024 - 256, true)]
        public async Task PersistenceProvider_Azure_WriteClearRead(int? stringLength, bool useJson)
        {
            var testName = string.Format("{0}({1} = {2}, {3} = {4})",
                nameof(PersistenceProvider_Azure_WriteClearRead),
                nameof(stringLength), stringLength == null ? "default" : stringLength.ToString(),
                nameof(useJson), useJson);

            var grainState = TestStoreGrainState.NewRandomState(stringLength);
            EnsureEnvironmentSupportsState(grainState);

            var store = await InitAzureTableGrainStorage(useJson);

            await Test_PersistenceProvider_WriteClearRead(testName, store, grainState);
        }

        [SkippableTheory, TestCategory("Functional"), TestCategory("AzureStorage")]
        [InlineData(null, true, false)]
        [InlineData(null, false, true)]
        [InlineData(15 * 32 * 1024 - 256, true, false)]
        [InlineData(15 * 32 * 1024 - 256, false, true)]
        public async Task PersistenceProvider_Azure_ChangeReadFormat(int? stringLength, bool useJsonForWrite, bool useJsonForRead)
        {
            var testName = string.Format("{0}({1} = {2}, {3} = {4}, {5} = {6})",
                nameof(PersistenceProvider_Azure_ChangeReadFormat),
                nameof(stringLength), stringLength == null ? "default" : stringLength.ToString(),
                nameof(useJsonForWrite), useJsonForWrite,
                nameof(useJsonForRead), useJsonForRead);

            var grainState = TestStoreGrainState.NewRandomState(stringLength);
            EnsureEnvironmentSupportsState(grainState);
            var grainId = LegacyGrainId.NewId();

            var store = await InitAzureTableGrainStorage(useJsonForWrite);

            grainState = await Test_PersistenceProvider_WriteRead(testName, store,
                grainState, grainId);

            store = await InitAzureTableGrainStorage(useJsonForRead);

            await Test_PersistenceProvider_Read(testName, store, grainState, grainId);
        }

        [SkippableTheory, TestCategory("Functional"), TestCategory("AzureStorage")]
        [InlineData(null, true, false)]
        [InlineData(null, false, true)]
        [InlineData(15 * 32 * 1024 - 256, true, false)]
        [InlineData(15 * 32 * 1024 - 256, false, true)]
        public async Task PersistenceProvider_Azure_ChangeWriteFormat(int? stringLength, bool useJsonForFirstWrite, bool useJsonForSecondWrite)
        {
            var testName = string.Format("{0}({1}={2},{3}={4},{5}={6})",
                nameof(PersistenceProvider_Azure_ChangeWriteFormat),
                nameof(stringLength), stringLength == null ? "default" : stringLength.ToString(),
                "json1stW", useJsonForFirstWrite,
                "json2ndW", useJsonForSecondWrite);

            var grainState = TestStoreGrainState.NewRandomState(stringLength);
            EnsureEnvironmentSupportsState(grainState);

            var grainId = LegacyGrainId.NewId();

            var store = await InitAzureTableGrainStorage(useJsonForFirstWrite);

            await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainId);

            grainState = TestStoreGrainState.NewRandomState(stringLength);
            grainState.ETag = "*";

            store = await InitAzureTableGrainStorage(useJsonForSecondWrite);

            await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainId);
        }

        [SkippableTheory, TestCategory("Functional"), TestCategory("AzureStorage")]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData(15 * 64 * 1024 - 256, false)]
        [InlineData(15 * 32 * 1024 - 256, true)]
        public async Task AzureTableStorage_ConvertToFromStorageFormat(int? stringLength, bool useJson)
        {
            var testName = string.Format("{0}({1} = {2}, {3} = {4})",
               nameof(AzureTableStorage_ConvertToFromStorageFormat),
               nameof(stringLength), stringLength == null ? "default" : stringLength.ToString(),
               nameof(useJson), useJson);

            var state = TestStoreGrainState.NewRandomState(stringLength);
            EnsureEnvironmentSupportsState(state);

            var storage = await InitAzureTableGrainStorage(useJson);
            var initialState = state.State;

            var entity = new TableEntity();

            storage.ConvertToStorageFormat(initialState, entity);

            var convertedState = storage.ConvertFromStorageFormat<TestStoreGrainState>(entity);
            Assert.NotNull(convertedState);
            Assert.Equal(initialState.A, convertedState.A);
            Assert.Equal(initialState.B, convertedState.B);
            Assert.Equal(initialState.C, convertedState.C);
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("AzureStorage")]
        public async Task AzureTableStorage_ConvertJsonToFromStorageFormatWithCustomJsonProperties()
        {
            TestUtils.CheckForAzureStorage();
            var state = TestStoreGrainStateWithCustomJsonProperties.NewRandomState(null);

            var storage = await InitAzureTableGrainStorage(useJson: true, typeNameHandling: TypeNameHandling.None);
            var initialState = state.State;

            var entity = new TableEntity();

            storage.ConvertToStorageFormat(initialState, entity);

            var convertedState = storage.ConvertFromStorageFormat<TestStoreGrainStateWithCustomJsonProperties>(entity);
            Assert.NotNull(convertedState);
            Assert.Equal(initialState.String, convertedState.String);
        }

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public async Task PersistenceProvider_Memory_FixedLatency_WriteRead()
        {
            const string testName = nameof(PersistenceProvider_Memory_FixedLatency_WriteRead);
            TimeSpan expectedLatency = TimeSpan.FromMilliseconds(200);
            MemoryGrainStorageWithLatency store = new MemoryGrainStorageWithLatency(testName, new MemoryStorageWithLatencyOptions()
            {
                Latency = expectedLatency,
                MockCallsOnly = true
            }, NullLoggerFactory.Instance, this.providerRuntime.ServiceProvider.GetService<IGrainFactory>());

            var reference = (GrainId)LegacyGrainId.NewId();
            var state = TestStoreGrainState.NewRandomState();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            await store.WriteStateAsync(testName, reference, state);
            TimeSpan writeTime = sw.Elapsed;
            this.output.WriteLine("{0} - Write time = {1}", store.GetType().FullName, writeTime);
            Assert.True(writeTime >= expectedLatency, $"Write: Expected minimum latency = {expectedLatency} Actual = {writeTime}");

            sw.Restart();
            var storedState = new GrainState<TestStoreGrainState>();
            await store.ReadStateAsync(testName, reference, storedState);
            TimeSpan readTime = sw.Elapsed;
            this.output.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);
            Assert.True(readTime >= expectedLatency, $"Read: Expected minimum latency = {expectedLatency} Actual = {readTime}");
        }

        [Fact, TestCategory("Functional")]
        public void LoadClassByName()
        {
            string className = typeof(MockStorageProvider).FullName;
            Type classType = new CachedTypeResolver().ResolveType(className);
            Assert.NotNull(classType); // Type
            Assert.True(typeof(IGrainStorage).IsAssignableFrom(classType), $"Is an IStorageProvider : {classType.FullName}");
        }

        private async Task<AzureTableGrainStorage> InitAzureTableGrainStorage(AzureTableStorageOptions options)
        {
            // TODO change test to include more serializer?
            var serializer = new OrleansGrainStorageSerializer(this.providerRuntime.ServiceProvider.GetRequiredService<Serializer>());
            AzureTableGrainStorage store = ActivatorUtilities.CreateInstance<AzureTableGrainStorage>(this.providerRuntime.ServiceProvider, options, serializer, "TestStorage");
            ISiloLifecycleSubject lifecycle = ActivatorUtilities.CreateInstance<SiloLifecycleSubject>(this.providerRuntime.ServiceProvider);
            store.Participate(lifecycle);
            await lifecycle.OnStart();
            return store;
        }

        private async Task<AzureTableGrainStorage> InitAzureTableGrainStorage(bool useJson = false, TypeNameHandling? typeNameHandling = null)
        {
            var options = new AzureTableStorageOptions();
            var jsonOptions = this.providerRuntime.ServiceProvider.GetService<IOptions<OrleansJsonSerializerOptions>>();
            if (typeNameHandling != null)
            {
                jsonOptions.Value.JsonSerializerSettings.TypeNameHandling = typeNameHandling.Value;
            }

            options.ConfigureTestDefaults();

            // TODO change test to include more serializer?
            var binarySerializer = new OrleansGrainStorageSerializer(this.providerRuntime.ServiceProvider.GetRequiredService<Serializer>());
            var jsonSerializer = new JsonGrainStorageSerializer(new OrleansJsonSerializer(jsonOptions));
            options.GrainStorageSerializer = useJson
                ? new GrainStorageSerializer(jsonSerializer, binarySerializer)
                : new GrainStorageSerializer(binarySerializer, jsonSerializer);

            AzureTableGrainStorage store = ActivatorUtilities.CreateInstance<AzureTableGrainStorage>(this.providerRuntime.ServiceProvider, options, "TestStorage");
            ISiloLifecycleSubject lifecycle = ActivatorUtilities.CreateInstance<SiloLifecycleSubject>(this.providerRuntime.ServiceProvider);
            store.Participate(lifecycle);
            await lifecycle.OnStart();
            return store;
        }

        private async Task Test_PersistenceProvider_Read(string grainTypeName, IGrainStorage store,
            GrainState<TestStoreGrainState> grainState = null, GrainId grainId = default)
        {
            var reference = grainId.IsDefault ? (GrainId)LegacyGrainId.NewId() : grainId;

            if (grainState == null)
            {
                grainState = new GrainState<TestStoreGrainState>(new TestStoreGrainState());
            }
            var storedGrainState = new GrainState<TestStoreGrainState>(new TestStoreGrainState());

            Stopwatch sw = new Stopwatch();
            sw.Start();

            await store.ReadStateAsync(grainTypeName, reference, storedGrainState);

            TimeSpan readTime = sw.Elapsed;
            this.output.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);

            var storedState = storedGrainState.State;
            Assert.Equal(grainState.State.A, storedState.A);
            Assert.Equal(grainState.State.B, storedState.B);
            Assert.Equal(grainState.State.C, storedState.C);
        }

        private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteRead(string grainTypeName,
            IGrainStorage store, GrainState<TestStoreGrainState> grainState = null, GrainId grainId = default)
        {
            var reference = grainId.IsDefault ? (GrainId)LegacyGrainId.NewId() : grainId;

            if (grainState == null)
            {
                grainState = TestStoreGrainState.NewRandomState();
            }

            Stopwatch sw = new Stopwatch();
            sw.Start();

            await store.WriteStateAsync(grainTypeName, reference, grainState);

            TimeSpan writeTime = sw.Elapsed;
            sw.Restart();

            var storedGrainState = new GrainState<TestStoreGrainState>
            {
                State = new TestStoreGrainState()
            };
            await store.ReadStateAsync(grainTypeName, reference, storedGrainState);
            TimeSpan readTime = sw.Elapsed;
            this.output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            Assert.Equal(grainState.State.A, storedGrainState.State.A);
            Assert.Equal(grainState.State.B, storedGrainState.State.B);
            Assert.Equal(grainState.State.C, storedGrainState.State.C);

            return storedGrainState;
        }

        private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteClearRead(string grainTypeName,
            IGrainStorage store, GrainState<TestStoreGrainState> grainState = null, GrainId grainId = default)
        {
            var reference = grainId.IsDefault ? (GrainId)LegacyGrainId.NewId() : grainId;

            if (grainState == null)
            {
                grainState = TestStoreGrainState.NewRandomState();
            }

            Stopwatch sw = new Stopwatch();
            sw.Start();

            await store.WriteStateAsync(grainTypeName, reference, grainState);

            TimeSpan writeTime = sw.Elapsed;
            sw.Restart();

            await store.ClearStateAsync(grainTypeName, reference, grainState);

            var storedGrainState = new GrainState<TestStoreGrainState>
            {
                State = new TestStoreGrainState()
            };
            await store.ReadStateAsync(grainTypeName, reference, storedGrainState);
            TimeSpan readTime = sw.Elapsed;
            this.output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            Assert.NotNull(storedGrainState.State);
            Assert.Equal(default(string), storedGrainState.State.A);
            Assert.Equal(default(int), storedGrainState.State.B);
            Assert.Equal(default(long), storedGrainState.State.C);

            return storedGrainState;
        }

        private static void EnsureEnvironmentSupportsState(GrainState<TestStoreGrainState> grainState)
        {
            if (grainState.State.A.Length > 400 * 1024)
            {
                StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();
            }

            TestUtils.CheckForAzureStorage();
        }

        public class TestStoreGrainStateWithCustomJsonProperties
        {
            [JsonProperty("s")]
            public string String { get; set; }

            internal static GrainState<TestStoreGrainStateWithCustomJsonProperties> NewRandomState(int? aPropertyLength = null)
            {
                return new GrainState<TestStoreGrainStateWithCustomJsonProperties>
                {
                    State = new TestStoreGrainStateWithCustomJsonProperties
                    {
                        String = aPropertyLength == null
                            ? Random.Shared.Next().ToString(CultureInfo.InvariantCulture)
                            : GenerateRandomDigitString(aPropertyLength.Value)
                    }
                };
            }

            private static string GenerateRandomDigitString(int stringLength)
            {
                var characters = new char[stringLength];
                for (var i = 0; i < stringLength; ++i)
                {
                    characters[i] = (char)Random.Shared.Next('0', '9' + 1);
                }
                return new string(characters);
            }
        }
    }
}
