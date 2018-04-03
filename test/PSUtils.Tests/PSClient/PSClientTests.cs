using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using OrleansPSUtils;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using System;
using TestExtensions;

namespace PSUtils.Tests
{
    using Orleans.Hosting;
    using System.Linq;

    public class PowershellHostFixture : BaseTestClusterFixture
    {
        public PowerShell Powershell { get; set; }
        public Runspace Runspace { get; set; }
        public ClientConfiguration ClientConfig { get; set; }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                ClientConfig = legacy.ClientConfiguration;
            });
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        public class SiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("MemoryStore");
            }
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
        }

        public override void Dispose()
        {
            try
            {
                var stopCommand = new Command("Stop-GrainClient");
                Powershell.Commands.Clear();
                Powershell.Commands.AddCommand(stopCommand);
                Powershell.Invoke();
            }
            catch
            {
            }
            finally
            {
                Powershell.Dispose();
                Runspace.Dispose();
                base.Dispose();
            }
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

        [Fact, TestCategory("SlowBVT"), TestCategory("Tooling")]
        public void ScriptCallTest()
        {
            _ps.Commands.AddScript(File.ReadAllText(@".\PSClient\PSClientTests.ps1"));
            _ps.Commands.AddParameter("clientConfig", _clientConfig);
            var results = _ps.Invoke();
            Assert.Equal(5, results.Count);

            // Stop-Client with no current/specified client should throw (outputting $didThrow = true).
            Assert.NotNull(results[0]);
            Assert.True((bool)results[0].BaseObject);

            // Client must true be initialized
            Assert.NotNull(results[1]);
            Assert.True((bool)results[1].BaseObject);

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
            Assert.False((bool)results[4].BaseObject);            
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Tooling")]
        public void GetGrainTest()
        {
            var startGrainClient = new Command("Start-GrainClient");
            startGrainClient.Parameters.Add("Config", _clientConfig);
            _ps.Commands.AddCommand(startGrainClient);
            var client = _ps.Invoke().FirstOrDefault()?.BaseObject as IClusterClient;
            Assert.NotNull(client);
            Assert.True(client.IsInitialized);
            _ps.Commands.Clear();

            var getGrainCommand = new Command("Get-Grain");
            getGrainCommand.Parameters.Add("GrainType", typeof(IManagementGrain));
            getGrainCommand.Parameters.Add("LongKey", (long)0);
            getGrainCommand.Parameters.Add("Client", client);
            _ps.Commands.AddCommand(getGrainCommand);

            var results = _ps.Invoke<IManagementGrain>();
            //Must be exactly 1 but powershell APIs always return a list
            Assert.Single(results);
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
            getGrainCommand.Parameters.Add("Client", client);
            _ps.Commands.AddCommand(getGrainCommand);

            var stringGrainsResults = _ps.Invoke<IStringGrain>();
            //Must be exactly 1 but powershell APIs always return a list
            Assert.Single(stringGrainsResults);
            _ps.Commands.Clear();

            var stringGrain = stringGrainsResults[0];
            Assert.NotNull(stringGrain);

            getGrainCommand.Parameters.Clear();
            getGrainCommand.Parameters.Add("GrainType", typeof(IGuidGrain));
            getGrainCommand.Parameters.Add("GuidKey", Guid.NewGuid());
            getGrainCommand.Parameters.Add("Client", client);
            _ps.Commands.AddCommand(getGrainCommand);

            var guidGrainsResults = _ps.Invoke<IGuidGrain>();
            //Must be exactly 1 but powershell APIs always return a list
            Assert.Single(guidGrainsResults);
            _ps.Commands.Clear();

            var guidGrain = guidGrainsResults[0];
            Assert.NotNull(guidGrain);

            this.StopGrainClient(client);
        }

        private void StopGrainClient(IClusterClient client)
        {
            var stopGrainClient = new Command("Stop-GrainClient");
            _ps.Commands.AddCommand(stopGrainClient).AddParameter("Client", client);
            _ps.Invoke();
            _ps.Commands.Clear();
            Assert.True(!client.IsInitialized);
        }
    }
}
