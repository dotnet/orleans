using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace TestExtensions.Runners
{
    public class GrainPersistenceTestsRunner : OrleansTestingBase
    {
        private readonly ITestOutputHelper output;
        private readonly string grainNamespace;
        private readonly BaseTestClusterFixture fixture;
        protected readonly ILogger logger;
        protected TestCluster HostedCluster { get; private set; }

        public GrainPersistenceTestsRunner(ITestOutputHelper output, BaseTestClusterFixture fixture, string grainNamespace = "UnitTests.Grains")
        {
            this.output = output;
            this.fixture = fixture;
            this.grainNamespace = grainNamespace;
            this.logger = fixture.Logger;
            HostedCluster = fixture.HostedCluster;
            GrainFactory = fixture.GrainFactory;
        }

        public IGrainFactory GrainFactory { get; }

        [Fact]
        public async Task Grain_GrainStorage_Delete()
        {
            Guid id = Guid.NewGuid();
            IGrainStorageTestGrain grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id, this.grainNamespace);

            await grain.DoWrite(1);

            await grain.DoDelete();

            int val = await grain.GetValue(); // Should this throw instead?
            Assert.Equal(0, val);  // "Value after Delete"

            await grain.DoWrite(2);

            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Delete + New Write"
        }

        [Fact]
        public async Task Grain_GrainStorage_Read()
        {
            Guid id = Guid.NewGuid();
            IGrainStorageTestGrain grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id, this.grainNamespace);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"
        }

        [Fact]
        public async Task Grain_GuidKey_GrainStorage_Read_Write()
        {
            Guid id = Guid.NewGuid();
            IGrainStorageTestGrain grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id, this.grainNamespace);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.Equal(1, val);  // "Value after Write-1"

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Write-2"

            val = await grain.DoRead();

            Assert.Equal(2, val);  // "Value after Re-Read"
        }

        [Fact]
        public async Task Grain_LongKey_GrainStorage_Read_Write()
        {
            long id = random.Next();
            IGrainStorageTestGrain_LongKey grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain_LongKey>(id, this.grainNamespace);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.Equal(1, val);  // "Value after Write-1"

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Write-2"

            val = await grain.DoRead();

            Assert.Equal(2, val);  // "Value after Re-Read"
        }

        [Fact]
        public async Task Grain_LongKeyExtended_GrainStorage_Read_Write()
        {
            long id = random.Next();
            string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

            IGrainStorageTestGrain_LongExtendedKey
                grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain_LongExtendedKey>(id, extKey, this.grainNamespace);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.Equal(1, val);  // "Value after Write-1"

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Write-2"

            val = await grain.DoRead();
            Assert.Equal(2, val);  // "Value after DoRead"

            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Re-Read"

            string extKeyValue = await grain.GetExtendedKeyValue();
            Assert.Equal(extKey, extKeyValue);  // "Extended Key"
        }

        [Fact]
        public async Task Grain_GuidKeyExtended_GrainStorage_Read_Write()
        {
            var id = Guid.NewGuid();
            string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

            IGrainStorageTestGrain_GuidExtendedKey
                grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain_GuidExtendedKey>(id, extKey, this.grainNamespace);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.Equal(1, val);  // "Value after Write-1"

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Write-2"

            val = await grain.DoRead();
            Assert.Equal(2, val);  // "Value after DoRead"

            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Re-Read"

            string extKeyValue = await grain.GetExtendedKeyValue();
            Assert.Equal(extKey, extKeyValue);  // "Extended Key"
        }

        [Fact]
        public async Task Grain_Generic_GrainStorage_Read_Write()
        {
            long id = random.Next();

            IGrainStorageGenericGrain<int> grain = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<int>>(id, this.grainNamespace);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.Equal(1, val);  // "Value after Write-1"

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Write-2"

            val = await grain.DoRead();

            Assert.Equal(2, val);  // "Value after Re-Read"
        }

        [Fact]
        public async Task Grain_NestedGeneric_GrainStorage_Read_Write()
        {
            long id = random.Next();

            IGrainStorageGenericGrain<List<int>> grain = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<List<int>>>(id, this.grainNamespace);

            var val = await grain.GetValue();

            Assert.Null(val);  // "Initial value"

            await grain.DoWrite(new List<int> { 1 });
            val = await grain.GetValue();
            Assert.Equal(new List<int> { 1 }, val);  // "Value after Write-1"

            await grain.DoWrite(new List<int> { 1, 2 });
            val = await grain.GetValue();
            Assert.Equal(new List<int> { 1, 2 }, val);  // "Value after Write-2"

            val = await grain.DoRead();

            Assert.Equal(new List<int> { 1, 2 }, val);  // "Value after Re-Read"
        }

        [Fact]
        public async Task Grain_Generic_GrainStorage_DiffTypes()
        {
            long id1 = random.Next();
            long id2 = id1;
            long id3 = id1;

            IGrainStorageGenericGrain<int> grain1 = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<int>>(id1, this.grainNamespace);

            IGrainStorageGenericGrain<string> grain2 = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<string>>(id2, this.grainNamespace);

            IGrainStorageGenericGrain<double> grain3 = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<double>>(id3, this.grainNamespace);

            int val1 = await grain1.GetValue();
            Assert.Equal(0, val1);  // "Initial value - 1"

            string val2 = await grain2.GetValue();
            Assert.Null(val2);  // "Initial value - 2"

            double val3 = await grain3.GetValue();
            Assert.Equal(0.0, val3);  // "Initial value - 3"

            int expected1 = 1;
            await grain1.DoWrite(expected1);
            val1 = await grain1.GetValue();
            Assert.Equal(expected1, val1);  // "Value after Write#1 - 1"

            string expected2 = "Three";
            await grain2.DoWrite(expected2);
            val2 = await grain2.GetValue();
            Assert.Equal(expected2, val2);  // "Value after Write#1 - 2"

            double expected3 = 5.1;
            await grain3.DoWrite(expected3);
            val3 = await grain3.GetValue();
            Assert.Equal(expected3, val3);  // "Value after Write#1 - 3"

            val1 = await grain1.GetValue();
            Assert.Equal(expected1, val1);  // "Value before Write#2 - 1"
            expected1 = 2;
            await grain1.DoWrite(expected1);
            val1 = await grain1.GetValue();
            Assert.Equal(expected1, val1);  // "Value after Write#2 - 1"
            val1 = await grain1.DoRead();
            Assert.Equal(expected1, val1);  // "Value after Re-Read - 1"

            val2 = await grain2.GetValue();
            Assert.Equal(expected2, val2);  // "Value before Write#2 - 2"
            expected2 = "Four";
            await grain2.DoWrite(expected2);
            val2 = await grain2.GetValue();
            Assert.Equal(expected2, val2);  // "Value after Write#2 - 2"
            val2 = await grain2.DoRead();
            Assert.Equal(expected2, val2);  // "Value after Re-Read - 2"

            val3 = await grain3.GetValue();
            Assert.Equal(expected3, val3);  // "Value before Write#2 - 3"
            expected3 = 6.2;
            await grain3.DoWrite(expected3);
            val3 = await grain3.GetValue();
            Assert.Equal(expected3, val3);  // "Value after Write#2 - 3"
            val3 = await grain3.DoRead();
            Assert.Equal(expected3, val3);  // "Value after Re-Read - 3"
        }
        
        [Fact]
        public async Task Grain_GrainStorage_SiloRestart()
        {
            var initialServiceId = fixture.GetClientServiceId();

            output.WriteLine("ClusterId={0} ServiceId={1}", this.HostedCluster.Options.ClusterId, initialServiceId);

            Guid id = Guid.NewGuid();
            IGrainStorageTestGrain grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id, this.grainNamespace);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"

            await grain.DoWrite(1);

            var serviceId = await this.GrainFactory.GetGrain<IServiceIdGrain>(Guid.Empty).GetServiceId();
            Assert.Equal(initialServiceId, serviceId);  // "ServiceId same before restart."

            output.WriteLine("About to reset Silos");
            foreach (var silo in this.HostedCluster.GetActiveSilos().ToList())
            {
                await this.HostedCluster.RestartSiloAsync(silo);
            }
            this.HostedCluster.InitializeClient();

            output.WriteLine("Silos restarted");

            serviceId = await this.GrainFactory.GetGrain<IServiceIdGrain>(Guid.Empty).GetServiceId();
            output.WriteLine("ClusterId={0} ServiceId={1}", this.HostedCluster.Options.ClusterId, serviceId);
            Assert.Equal(initialServiceId, serviceId);  // "ServiceId same after restart."

            val = await grain.GetValue();
            Assert.Equal(1, val);  // "Value after Write-1"

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.Equal(2, val);  // "Value after Write-2"

            val = await grain.DoRead();

            Assert.Equal(2, val);  // "Value after Re-Read"
        }
    }
}
