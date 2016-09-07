using System;
using System.Globalization;
using System.Reflection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;
using Orleans.TestingHost;
using Xunit;

namespace Tester
{
    public class SiloInitializationTests
    {
        /// <summary>
        /// Tests that a silo host can be successfully started after a prior initialization failure.
        /// </summary>
        [Fact]
        public void SiloInitializationIsRetryableTest()
        {
            var appDomain = CreateAppDomain();
            appDomain.UnhandledException += (sender, args) =>
            {
                throw new AggregateException("Exception from AppDomain", (Exception) args.ExceptionObject);
            };

            try
            {
                var config = new TestClusterOptions(1).ClusterConfiguration;
                var originalLivenessType = config.Globals.LivenessType;
                var originalMembershipAssembly = config.Globals.MembershipTableAssembly;

                // Set a configuration which will cause an early initialization error.
                // Try initializing the cluster, verify that it fails.
                config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.Custom;
                config.Globals.MembershipTableAssembly = "NonExistentAssembly.jpg";

                var siloHost = CreateSiloHost(appDomain, config);
                siloHost.InitializeOrleansSilo();

                // Attempt to start the silo.
                Assert.ThrowsAny<Exception>(() => siloHost.StartOrleansSilo(catchExceptions: false));
                siloHost.UnInitializeOrleansSilo();

                // Reset the configuration to a valid configuration.
                config.Globals.LivenessType = originalLivenessType;
                config.Globals.MembershipTableAssembly = originalMembershipAssembly;

                // Starting a new cluster should succeed.
                siloHost = CreateSiloHost(appDomain, config);
                siloHost.InitializeOrleansSilo();
                siloHost.StartOrleansSilo(catchExceptions: false);
            }
            finally
            {
                AppDomain.Unload(appDomain);
            }
        }

        private static AppDomain CreateAppDomain()
        {
            var currentAppDomain = AppDomain.CurrentDomain;
            var appDomainSetup = new AppDomainSetup
            {
                ApplicationBase = Environment.CurrentDirectory,
                ConfigurationFile = currentAppDomain.SetupInformation.ConfigurationFile,
                ShadowCopyFiles = currentAppDomain.SetupInformation.ShadowCopyFiles,
                ShadowCopyDirectories = currentAppDomain.SetupInformation.ShadowCopyDirectories,
                CachePath = currentAppDomain.SetupInformation.CachePath
            };

            return AppDomain.CreateDomain(nameof(SiloInitializationIsRetryableTest), null, appDomainSetup);
        }

        private static SiloHost CreateSiloHost(AppDomain appDomain, ClusterConfiguration clusterConfig)
        {
            var args = new object[] { nameof(SiloInitializationIsRetryableTest), clusterConfig };

            return (SiloHost)appDomain.CreateInstanceFromAndUnwrap(
                "OrleansRuntime.dll",
                typeof(SiloHost).FullName,
                false,
                BindingFlags.Default,
                null,
                args,
                CultureInfo.CurrentCulture,
                new object[] { });
        }
    }
}