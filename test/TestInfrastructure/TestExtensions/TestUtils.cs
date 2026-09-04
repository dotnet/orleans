using System.Diagnostics;
using System.Net;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;
using static TestExtensions.TestDefaultConfiguration;

namespace Tester
{
    public class TestUtils
    {
        public static long GetRandomGrainId() => Random.Shared.Next();

        public static void CheckForAzureStorage()
        {
            if (UseAadAuthentication)
            {
                GetValue(nameof(TableEndpoint), out var tableEndpoint);
                GetValue(nameof(DataBlobUri), out var dataBlobUri);
                GetValue(nameof(DataQueueUri), out var dataQueueUri);
                if (GetAzureStorageAadConfigurationError(tableEndpoint, dataBlobUri, dataQueueUri) is { } error)
                {
                    throw Xunit.Sdk.SkipException.ForSkip(error);
                }

                return;
            }

            if (UseAzurite)
            {
                AzuriteContainerManager.EnsureStarted();
                return;
            }

            var usingLocalStorageEmulator = string.Equals(DataConnectionString, "UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase);
            if (usingLocalStorageEmulator && !StorageEmulator.TryStart())
            {
                const string errorMessage = "Azure Storage Emulator could not be started.";
                Console.WriteLine(errorMessage);
                throw Xunit.Sdk.SkipException.ForSkip(errorMessage);
            }
        }

        internal static string? GetAzureStorageAadConfigurationError(
            string? tableEndpoint,
            string? dataBlobUri,
            string? dataQueueUri)
        {
            var invalidSettings = new List<string>(3);
            AddInvalidAbsoluteUri(invalidSettings, nameof(TableEndpoint), tableEndpoint);
            AddInvalidAbsoluteUri(invalidSettings, nameof(DataBlobUri), dataBlobUri);
            AddInvalidAbsoluteUri(invalidSettings, nameof(DataQueueUri), dataQueueUri);

            return invalidSettings.Count == 0
                ? null
                : $"AAD Azure Storage tests require valid absolute service endpoints. Missing or invalid settings: {string.Join(", ", invalidSettings)}.";
        }

        private static void AddInvalidAbsoluteUri(List<string> invalidSettings, string settingName, string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out _))
            {
                invalidSettings.Add(settingName);
            }
        }

        public static void CheckForEventHub()
        {
            if ((UseAadAuthentication && string.IsNullOrWhiteSpace(EventHubFullyQualifiedNamespace)) ||
                (!UseAadAuthentication && string.IsNullOrWhiteSpace(EventHubConnectionString)))
            {
                throw Xunit.Sdk.SkipException.ForSkip("No connection string found. Skipping");
            }
        }

        public static void CheckForRedis()
        {
            if (string.IsNullOrWhiteSpace(RedisConnectionString))
            {
                throw Xunit.Sdk.SkipException.ForSkip("No connection string found. Skipping");
            }
        }

        public static double CalibrateTimings()
        {
            const int NumLoops = 10000;
            TimeSpan baseline = TimeSpan.FromTicks(80); // Baseline from jthelin03D
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < NumLoops; i++)
            {
            }
            sw.Stop();
            double multiple = 1.0 * sw.ElapsedTicks / baseline.Ticks;
            Console.WriteLine("CalibrateTimings: {0} [{1} Ticks] vs {2} [{3} Ticks] = x{4}",
                sw.Elapsed, sw.ElapsedTicks,
                baseline, baseline.Ticks,
                multiple);
            return multiple > 1.0 ? multiple : 1.0;
        }

        public static async Task<TimeSpan> TimeRunAsync(int numIterations, TimeSpan baseline, string what, Func<Task> action)
        {
            var stopwatch = new Stopwatch();

            long startMem = GC.GetTotalMemory(true);
            stopwatch.Start();

            await action();

            stopwatch.Stop();
            long stopMem = GC.GetTotalMemory(false);
            long memUsed = stopMem - startMem;
            TimeSpan duration = stopwatch.Elapsed;

            string timeDeltaStr = "";
            if (baseline > TimeSpan.Zero)
            {
                double delta = (duration - baseline).TotalMilliseconds / baseline.TotalMilliseconds;
                timeDeltaStr = string.Format("-- Change = {0}%", 100.0 * delta);
            }
            Console.WriteLine("Time for {0} loops doing {1} = {2} {3} Memory used={4}", numIterations, what, duration, timeDeltaStr, memUsed);
            return duration;
        }

        public static async Task<int> GetActivationCount(IGrainFactory grainFactory, string grainTypeName)
        {
            int result = 0;

            IManagementGrain mgmtGrain = grainFactory.GetGrain<IManagementGrain>(0);
            SimpleGrainStatistic[] stats = await mgmtGrain.GetSimpleGrainStatistics();
            foreach (var stat in stats)
            {
                if (string.Equals(stat.GrainType, grainTypeName, StringComparison.Ordinal))
                {
                    result += stat.ActivationCount;
                }
            }
            return result;
        }
    }

    public static class RequestContextTestUtils
    {
        public static void SetActivityId(Guid id)
        {
            RequestContext.ReentrancyId = id;
        }

        public static Guid GetActivityId()
        {
            return RequestContext.ReentrancyId is Guid value ? value : Guid.Empty;
        }

        public static void ClearActivityId()
        {
            RequestContext.ReentrancyId = Guid.Empty;
        }
    }
}
