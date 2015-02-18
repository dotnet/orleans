using System;
using System.Collections.Generic;

using LoadTestGrainInterfaces;

namespace StreamPullingAgentBenchmark.EmbeddedSiloLoadTest
{
    public static class EmbeddedSiloManager
    {
        private static BaseOptions _options;

        private static OrleansHostWrapper _hostWrapper;

        public static IEnumerable<AppDomain> StartEmbeddedSilos(BaseOptions options, string[] args)
        {
            _options = options;

            if (_options.EmbedSilos < 1)
            {
                throw new ArgumentOutOfRangeException("embed-silos", _options.EmbedSilos, "Silo count is less than 1");
            }
            List<AppDomain> result = new List<AppDomain>();
            for (int i = 0; i < _options.EmbedSilos; ++i)
            {
                result.Add(StartEmbeddedSilo(i, args));
            }
            return result;
        }

        private static AppDomain StartEmbeddedSilo(int siloId, string[] args)
        {
            // The Orleans silo environment is initialized in its own app domain in order to more
            // closely emulate the distributed situation, when the client and the server cannot
            // pass data via shared memory.
            string siloName = String.Format("Silo{0:d2}", siloId);

            AppDomain appDomain = AppDomain.CreateDomain(siloName, null, new AppDomainSetup
            {
                AppDomainInitializer = StartSiloHost,
                AppDomainInitializerArguments = args
            });
            return appDomain;
        }

        private static void StartSiloHost(string[] args)
        {
            string siloName = AppDomain.CurrentDomain.FriendlyName;
            BaseOptions options;
            if (!Utilities.ParseArguments(args, out options))
            {
                Console.Error.WriteLine("Failed to initialize Orleans silo - bad arguments");
            }
            _hostWrapper = new OrleansHostWrapper(options.SiloConfigFile, siloName, options.DeploymentId, options.Verbose);

            if (!_hostWrapper.Run())
            {
                Console.Error.WriteLine("Failed to initialize Orleans silo");
            }
        }

        public static void StopEmbeddedSilos(IEnumerable<AppDomain> hostDomains)
        {
            foreach (var domain in hostDomains)
            {
                domain.DoCallBack(StopEmbeddedSilo);
            }
        }

        private static void StopEmbeddedSilo()
        {
            if (_hostWrapper != null)
            {
                _hostWrapper.Dispose();
            }
        }
    }
}