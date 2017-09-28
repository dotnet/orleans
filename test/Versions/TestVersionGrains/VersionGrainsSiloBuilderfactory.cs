using System;
using System.IO;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Placement;
using UnitTests.GrainInterfaces;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Utils;
using UnitTests.Grains;

namespace TestVersionGrains
{
    public class VersionGrainsSiloBuilderFactory : ISiloBuilderFactory
    {
        public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
        {
            return new SiloHostBuilder()
                .ConfigureSiloName(siloName)
                .UseConfiguration(clusterConfiguration)
                .ConfigureServices(this.ConfigureServices)
                .AddApplicationPartsFromAppDomain()
                .AddApplicationPartsFromBasePath()
                .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, GetLegacyTraceFileName(siloName, DateTime.UtcNow)));
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IPlacementDirector<VersionAwarePlacementStrategy>, VersionAwarePlacementDirector>();
        }

        private static string GetLegacyTraceFileName(string nodeName, DateTime timestamp, string traceFileFolder = "logs", string traceFilePattern = "{0}-{1}.log")
        {
            const string dateFormat = "yyyy-MM-dd-HH.mm.ss.fffZ";
            string traceFileName = null;
            if (traceFilePattern == null
                || string.IsNullOrWhiteSpace(traceFilePattern)
                || traceFilePattern.Equals("false", StringComparison.OrdinalIgnoreCase)
                || traceFilePattern.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                //default trace file pattern
                traceFilePattern = "{0}-{1}.log";
            }

            string traceFileDir = Path.GetDirectoryName(traceFilePattern);
            if (!String.IsNullOrEmpty(traceFileDir) && !Directory.Exists(traceFileDir))
            {
                traceFileName = Path.GetFileName(traceFilePattern);
                string[] alternateDirLocations = { "appdir", "." };
                foreach (var d in alternateDirLocations)
                {
                    if (Directory.Exists(d))
                    {
                        traceFilePattern = Path.Combine(d, traceFileName);
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(traceFileFolder) && !Directory.Exists(traceFileFolder))
            {
                Directory.CreateDirectory(traceFileFolder);
            }

            traceFilePattern = $"{traceFileFolder}\\{traceFilePattern}";
            traceFileName = String.Format(traceFilePattern, nodeName, timestamp.ToUniversalTime().ToString(dateFormat), Dns.GetHostName());

            return traceFileName;
        }
    }
}
