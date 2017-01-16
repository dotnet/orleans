using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester;
using Tester.TestStreamProviders;
using TestExtensions;
using TestGrainInterfaces;
using TestGrains;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.StreamingTests
{
    // if we parallelize tests, each test should run in isolation 
    public class DynamicStreamProviderConfigurationTests : OrleansTestingBase, IClassFixture<DynamicStreamProviderConfigurationTests.Fixture>, IDisposable
    {
        private readonly Fixture fixture;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private IManagementGrain mgmtGrain;
        private const string streamProviderName1 = "GeneratedStreamProvider1";
        private const string streamProviderName2 = "GeneratedStreamProvider2";

        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamNamespace = GeneratedEventCollectorGrain.StreamNamespace;
            public Dictionary<string, string> DefaultStreamProviderSettings = new Dictionary<string, string>();

            public static readonly SimpleGeneratorConfig GeneratorConfig = new SimpleGeneratorConfig
            {
                StreamNamespace = StreamNamespace,
                EventsInStream = 100
            };

            public static readonly GeneratorAdapterConfig AdapterConfig = new GeneratorAdapterConfig(GeneratedStreamTestConstants.StreamProviderName)
            {
                TotalQueueCount = 4,
                GeneratorConfigType = GeneratorConfig.GetType()
            };

            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(1);
                // get initial settings from configs
                AdapterConfig.WriteProperties(DefaultStreamProviderSettings);
                GeneratorConfig.WriteProperties(DefaultStreamProviderSettings);

                // add queue balancer setting
                DefaultStreamProviderSettings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

                // add pub/sub settting
                DefaultStreamProviderSettings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());

                return new TestCluster(options);
            }
        }

        public void Dispose()
        {
            RemoveAllProviders().WaitWithThrow(Timeout);
        }

        public DynamicStreamProviderConfigurationTests(Fixture fixture)
        {
            this.fixture = fixture;
            RemoveAllProviders().WaitWithThrow(Timeout);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public async Task DynamicStreamProviderConfigurationTests_AddAndRemoveProvidersAndCheckCounters()
        {
            //Making sure the initial provider list is empty.
            List<string> providerNames = (await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames()).ToList();
            Assert.Equal(0, providerNames.Count);

            providerNames = new List<string>(new [] { GeneratedStreamTestConstants.StreamProviderName });
            var reporter = this.fixture.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
            try
            {
                await AddGeneratorStreamProviderAndVerify(providerNames);
                await TestingUtils.WaitUntilAsync(CheckCounters, Timeout);

                await reporter.Reset();
                await RemoveProvidersAndVerify(providerNames);
                await AddGeneratorStreamProviderAndVerify(providerNames);
                await TestingUtils.WaitUntilAsync(CheckCounters, Timeout);
            }
            finally
            {
                await reporter.Reset();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public async Task DynamicStreamProviderConfigurationTests_AddAndRemoveProvidersInBatch()
        {
            //Making sure the initial provider list is empty.
            ICollection<string> providerNames = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            Assert.Equal(0, providerNames.Count);

            providerNames = new List<string>(new[]
            {
                GeneratedStreamTestConstants.StreamProviderName,
                streamProviderName1,
                streamProviderName2
            });
            await AddGeneratorStreamProviderAndVerify(providerNames);
            await RemoveProvidersAndVerify(providerNames);
            providerNames = new List<string>(new[]
            {
                streamProviderName1,
                streamProviderName2
            });
            await AddGeneratorStreamProviderAndVerify(providerNames);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public async Task DynamicStreamProviderConfigurationTests_AddAndRemoveProvidersIndividually()
        {
            //Making sure the initial provider list is empty.
            ICollection<string> providerNames = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            Assert.Equal(0, providerNames.Count);

            providerNames = new List<string>(new[] { GeneratedStreamTestConstants.StreamProviderName });
            await AddGeneratorStreamProviderAndVerify(providerNames);

            providerNames = new List<string>(new [] { streamProviderName1 });
            await AddGeneratorStreamProviderAndVerify(providerNames);

            providerNames = new List<string>(new [] { streamProviderName2 });
            await AddGeneratorStreamProviderAndVerify(providerNames);

            providerNames = new List<string>(new[] { streamProviderName2 });
            await RemoveProvidersAndVerify(providerNames);

            providerNames = new List<string>(new[] { streamProviderName2 });
            await AddGeneratorStreamProviderAndVerify(providerNames);

            providerNames = new List<string>(new[] { streamProviderName1 });
            await RemoveProvidersAndVerify(providerNames);

            providerNames = new List<string>(new[] { GeneratedStreamTestConstants.StreamProviderName });
            await RemoveProvidersAndVerify(providerNames);

            providerNames = new List<string>(new[] { GeneratedStreamTestConstants.StreamProviderName });
            await AddGeneratorStreamProviderAndVerify(providerNames);

            providerNames = new List<string>(new[] { streamProviderName1 });
            await AddGeneratorStreamProviderAndVerify(providerNames);

            providerNames = new List<string>(new[] { streamProviderName1 });
            await RemoveProvidersAndVerify(providerNames);

            providerNames = new List<string>(new[] { GeneratedStreamTestConstants.StreamProviderName });
            await RemoveProvidersAndVerify(providerNames);

        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public async Task DynamicStreamProviderConfigurationTests_AddProvidersAndThrowExceptionOnInitialization()
        {
            //Making sure the initial provider list is empty.
            ICollection<string> providerNames = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            Assert.Equal(0, providerNames.Count);

            Dictionary<string, string> providerSettings =
                new Dictionary<string, string>(fixture.DefaultStreamProviderSettings)
                {
                    {
                        FailureInjectionStreamProvider.FailureInjectionModeString,
                        FailureInjectionStreamProviderMode.InitializationThrowsException.ToString()
                    }
                };
            providerNames = new List<string>(new [] {"FailureInjectionStreamProvider"});

            await Assert.ThrowsAsync<ProviderInitializationException>(() =>
                AddFailureInjectionStreamProviderAndVerify(providerNames, providerSettings));
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public async Task DynamicStreamProviderConfigurationTests_AddProvidersAndThrowExceptionOnStart()
        {
            //Making sure the initial provider list is empty.
            ICollection<string> providerNames = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            Assert.Equal(0, providerNames.Count);
            Dictionary<string, string> providerSettings =
                new Dictionary<string, string>(fixture.DefaultStreamProviderSettings)
                {
                    {
                        FailureInjectionStreamProvider.FailureInjectionModeString,
                        FailureInjectionStreamProviderMode.StartThrowsException.ToString()
                    }
                };
            providerNames = new List<string>(new [] { "FailureInjectionStreamProvider"});
            await Assert.ThrowsAsync<ProviderStartException>(() =>
                AddFailureInjectionStreamProviderAndVerify(providerNames, providerSettings));
        }

        private async Task RemoveAllProviders()
        {
            ICollection<string> providerNames = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            await RemoveProvidersAndVerify(providerNames);
            providerNames = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            Assert.Equal(0, providerNames.Count);
        }

        private async Task AddFailureInjectionStreamProviderAndVerify(ICollection<string> streamProviderNames, Dictionary<string, string> ProviderSettings)
        {
            foreach (string providerName in streamProviderNames)
            {
                fixture.HostedCluster.ClusterConfiguration.Globals.RegisterStreamProvider<FailureInjectionStreamProvider>(providerName, ProviderSettings);
            }
            await AddProvidersAndVerify(streamProviderNames);
        }

        private async Task AddGeneratorStreamProviderAndVerify(ICollection<string> streamProviderNames)
        {
            foreach (string providerName in streamProviderNames)
            {
                fixture.HostedCluster.ClusterConfiguration.Globals.RegisterStreamProvider<GeneratorStreamProvider>(providerName, fixture.DefaultStreamProviderSettings);
            }
            await AddProvidersAndVerify(streamProviderNames);
        }

        private async Task AddProvidersAndVerify(ICollection<string> streamProviderNames)
        {
            this.mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            ICollection<string> names = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();

            IDictionary<string, bool> hasNewProvider = new Dictionary<string, bool>();

            int count = names.Count;
            SiloAddress[] address = new SiloAddress[1];
            address[0] = fixture.HostedCluster.Primary.SiloAddress;
            await mgmtGrain.UpdateStreamProviders(address, fixture.HostedCluster.ClusterConfiguration.Globals.ProviderConfigurations);
            names = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            ICollection<string> allSiloProviderNames = await fixture.HostedCluster.Primary.TestHook.GetAllSiloProviderNames();
            Assert.Equal(names.Count - count, streamProviderNames.Count);
            Assert.Equal(allSiloProviderNames.Count, names.Count);
            foreach (string name in names)
            {
                if (streamProviderNames.Contains(name))
                {
                    Assert.DoesNotContain(name, hasNewProvider.Keys);
                    hasNewProvider[name] = true;
                }
            }

            Assert.Equal(hasNewProvider.Count, streamProviderNames.Count);

            hasNewProvider.Clear();
            foreach (String name in allSiloProviderNames)
            {
                if (streamProviderNames.Contains(name))
                {
                    Assert.DoesNotContain(name, hasNewProvider.Keys);
                    hasNewProvider[name] = true;
                }
            }
            Assert.Equal(hasNewProvider.Count, streamProviderNames.Count);
        }

        private async Task RemoveProvidersAndVerify(ICollection<string> streamProviderNames)
        {
            this.mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            ICollection<string> names = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            int Count = names.Count;
            foreach (string name in streamProviderNames)
            {
                if(fixture.HostedCluster.ClusterConfiguration.Globals.ProviderConfigurations[ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME].Providers.ContainsKey(name))
                    fixture.HostedCluster.ClusterConfiguration.Globals.ProviderConfigurations[ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME].Providers.Remove(name);
            }

            SiloAddress[] address = new SiloAddress[1];
            address[0] = fixture.HostedCluster.Primary.SiloAddress;
            await mgmtGrain.UpdateStreamProviders(address, fixture.HostedCluster.ClusterConfiguration.Globals.ProviderConfigurations);
            names = await fixture.HostedCluster.Primary.TestHook.GetStreamProviderNames();
            ICollection<string> allSiloProviderNames = await fixture.HostedCluster.Primary.TestHook.GetAllSiloProviderNames();
            Assert.Equal(Count - names.Count, streamProviderNames.Count);
            Assert.Equal(allSiloProviderNames.Count, names.Count);
            foreach (String name in streamProviderNames)
            {
                Assert.DoesNotContain(name, names);;
            }
            foreach (String name in streamProviderNames)
            {
                Assert.DoesNotContain(name, allSiloProviderNames);
            }
        }

        private async Task<bool> CheckCounters(bool assertIsTrue)
        {
            var reporter = this.fixture.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(GeneratedStreamTestConstants.StreamProviderName, Fixture.StreamNamespace);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.Equal(Fixture.AdapterConfig.TotalQueueCount, report.Count);
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.Equal(Fixture.GeneratorConfig.EventsInStream, eventsPerStream);
                }
            }
            else if (Fixture.AdapterConfig.TotalQueueCount != report.Count ||
                     report.Values.Any(count => count != Fixture.GeneratorConfig.EventsInStream))
            {
                return false;
            }
            return true;
        }
    }
}
