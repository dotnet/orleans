﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace TestExtensions.Runners
{
    public class GrainPersistenceTestsRunner : OrleansTestingBase
    {
        private Guid serviceID;
        private readonly ITestOutputHelper output;
        protected TestCluster HostedCluster { get; private set; }
        protected readonly ILogger logger;
        public GrainPersistenceTestsRunner(ITestOutputHelper output, BaseTestClusterFixture fixture, Guid serviceId)
        {
            this.output = output;
            this.logger = fixture.Logger;
            HostedCluster = fixture.HostedCluster;
            GrainFactory = fixture.GrainFactory;
            this.serviceID = serviceId;
        }

        public IGrainFactory GrainFactory { get; }

        [Fact]
        public async Task Grain_GrainStorage_Delete()
        {
            Guid id = Guid.NewGuid();
            IGrainStorageTestGrain grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id);

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
            IGrainStorageTestGrain grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"
        }
        [Fact]
        public async Task Grain_GuidKey_GrainStorage_Read_Write()
        {
            Guid id = Guid.NewGuid();
            IGrainStorageTestGrain grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id);

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
            IGrainStorageTestGrain_LongKey grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain_LongKey>(id);

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
                grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain_LongExtendedKey>(id, extKey, null);

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
                grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain_GuidExtendedKey>(id, extKey, null);

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

            IGrainStorageGenericGrain<int> grain = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<int>>(id);

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
        public async Task Grain_Generic_GrainStorage_DiffTypes()
        {
            long id1 = random.Next();
            long id2 = id1;
            long id3 = id1;

            IGrainStorageGenericGrain<int> grain1 = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<int>>(id1);

            IGrainStorageGenericGrain<string> grain2 = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<string>>(id2);

            IGrainStorageGenericGrain<double> grain3 = this.GrainFactory.GetGrain<IGrainStorageGenericGrain<double>>(id3);

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
            var initialServiceId = serviceID;

            output.WriteLine("ClusterId={0} ServiceId={1}", this.HostedCluster.Options.ClusterId, serviceID);

            Guid id = Guid.NewGuid();
            IGrainStorageTestGrain grain = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id);

            int val = await grain.GetValue();

            Assert.Equal(0, val);  // "Initial value"

            await grain.DoWrite(1);

            output.WriteLine("About to reset Silos");
            foreach (var silo in this.HostedCluster.GetActiveSilos().ToList())
            {
                this.HostedCluster.RestartSilo(silo);
            }
            this.HostedCluster.InitializeClient();

            output.WriteLine("Silos restarted");

            var serviceId = await this.GrainFactory.GetGrain<IServiceIdGrain>(Guid.Empty).GetServiceId();
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
