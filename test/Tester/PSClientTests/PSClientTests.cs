using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using OrleansPSUtils;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using System;

namespace Tester
{
    public class PowershellHostFixture : BaseTestClusterFixture
    {
        public PowerShell Powershell { get; set; }
        public Runspace Runspace { get; set; }
        public ClientConfiguration ClientConfig { get; set; }

        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
            ClientConfig = TestClusterOptions.BuildClientConfiguration(options.ClusterConfiguration);
            return new TestCluster(options.ClusterConfiguration, null);
        }

        public PowershellHostFixture()
        {
            var initialSessionState = InitialSessionState.CreateDefault();
            initialSessionState.Commands.Add(new SessionStateCmdletEntry("Start-GrainClient", typeof(StartGrainClient), null));
            initialSessionState.Commands.Add(new SessionStateCmdletEntry("Stop-GrainClient", typeof(StopGrainClient), null));
            initialSessionState.Commands.Add(new SessionStateCmdletEntry("Get-Grain", typeof(GetGrain), null));
            Runspace = RunspaceFactory.CreateRunspace(initialSessionState);
            Runspace.Open();
            Powershell = PowerShell.Create();
            Powershell.Runspace = Runspace;

            var stopGrainClient = new Command("Stop-GrainClient");
            Powershell.Commands.AddCommand(stopGrainClient);
            Powershell.Invoke();
        }

        public override void Dispose()
        {
            var stopCommand = new Command("Stop-GrainClient");
            Powershell.Commands.Clear();
            Powershell.Commands.AddCommand(stopCommand);
            Powershell.Invoke();
            Powershell.Dispose();
            Runspace.Dispose();
            base.Dispose();
        }
    }

    public class PSClientTests : OrleansTestingBase, IClassFixture<PowershellHostFixture>
    {
        private PowerShell _ps;
        private ClientConfiguration _clientConfig;
        private ITestOutputHelper output;

        public PSClientTests(ITestOutputHelper output, PowershellHostFixture fixture)
        {
            this.output = output;
            _clientConfig = fixture.ClientConfig;
            _ps = fixture.Powershell;
            _ps.Commands.Clear();
        }

        [Fact, TestCategory("BVT"), TestCategory("Tooling")]
        public void ScriptCallTest()
        {
            _ps.Commands.AddScript(File.ReadAllText(@".\PSClientTests\PSClientTests.ps1"));
            _ps.Commands.AddParameter("clientConfig", _clientConfig);
            var results = _ps.Invoke();
            Assert.True(results.Count == 5);

            // Client must not be initialized
            Assert.NotNull(results[0]);
            Assert.True((bool)results[0].BaseObject == false);

            // Client must true be initialized
            Assert.NotNull(results[1]);
            Assert.True((bool)results[1].BaseObject == true);

            // The grain reference must not be null and of type IManagementGrain
            Assert.NotNull(results[2]);
            Assert.True(results[2].BaseObject is IManagementGrain);
            
            var statuses = results[3].BaseObject as Dictionary<SiloAddress, SiloStatus>;
            Assert.NotNull(statuses);
            Assert.True(statuses.Count > 0);
            foreach (var pair in statuses)
            {
                output.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.Equal(
                    SiloStatus.Active,
                    pair.Value);
            }

            // Client must be not initialized
            Assert.NotNull(results[4]);
            Assert.True((bool)results[4].BaseObject == GrainClient.IsInitialized);            
        }

        [Fact, TestCategory("BVT"), TestCategory("Tooling")]
        public void GetGrainTest()
        {
            StopGrainClient();

            var startGrainClient = new Command("Start-GrainClient");
            startGrainClient.Parameters.Add("Config", _clientConfig);
            _ps.Commands.AddCommand(startGrainClient);
            _ps.Invoke();
            Assert.True(GrainClient.IsInitialized);
            _ps.Commands.Clear();

            var getGrainCommand = new Command("Get-Grain");
            getGrainCommand.Parameters.Add("GrainType", typeof(IManagementGrain));
            getGrainCommand.Parameters.Add("LongKey", RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
            _ps.Commands.AddCommand(getGrainCommand);

            var results = _ps.Invoke<IManagementGrain>();
            //Must be exactly 1 but powershell APIs always return a list
            Assert.Equal(results.Count, 1);
            _ps.Commands.Clear();

            var mgmtGrain = results[0];

            //From now on, it is just a regular grain call
            Dictionary<SiloAddress, SiloStatus> statuses = mgmtGrain.GetHosts(onlyActive: true).Result;
            foreach (var pair in statuses)
            {
                output.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.Equal(
                    SiloStatus.Active,
                    pair.Value);
            }
            Assert.True(statuses.Count > 0);

            getGrainCommand.Parameters.Clear();
            getGrainCommand.Parameters.Add("GrainType", typeof(IStringGrain));
            getGrainCommand.Parameters.Add("StringKey", "myKey");
            _ps.Commands.AddCommand(getGrainCommand);

            var stringGrainsResults = _ps.Invoke<IStringGrain>();
            //Must be exactly 1 but powershell APIs always return a list
            Assert.Equal(stringGrainsResults.Count, 1);
            _ps.Commands.Clear();

            var stringGrain = stringGrainsResults[0];
            Assert.NotNull(stringGrain);

            getGrainCommand.Parameters.Clear();
            getGrainCommand.Parameters.Add("GrainType", typeof(IGuidGrain));
            getGrainCommand.Parameters.Add("GuidKey", Guid.NewGuid());
            _ps.Commands.AddCommand(getGrainCommand);

            var guidGrainsResults = _ps.Invoke<IGuidGrain>();
            //Must be exactly 1 but powershell APIs always return a list
            Assert.Equal(guidGrainsResults.Count, 1);
            _ps.Commands.Clear();

            var guidGrain = guidGrainsResults[0];
            Assert.NotNull(guidGrain);

            StopGrainClient();
        }

        private void StopGrainClient()
        {
            var stopGrainClient = new Command("Stop-GrainClient");
            _ps.Commands.AddCommand(stopGrainClient);
            _ps.Invoke();
            _ps.Commands.Clear();
            Assert.True(!GrainClient.IsInitialized);
        }
    }
}
