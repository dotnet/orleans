using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace OrleansTelemetryConsumers.Counters
{
    /// <summary>
    /// Telemetry consumer that writes metrics to predefined performance counters.
    /// </summary>
    public class OrleansPerfCounterTelemetryConsumer : IMetricTelemetryConsumer
    {
        internal const string CATEGORY_NAME = "OrleansRuntime";
        internal const string CATEGORY_DESCRIPTION = "Orleans Runtime Counters";
        private const string ExplainHowToCreateOrleansPerfCounters = "Run 'InstallUtil.exe OrleansTelemetryConsumers.Counters.dll' as Administrator to create perf counters for Orleans.";

        private readonly ILogger logger;
        private readonly List<PerfCounterConfigData> perfCounterData = new List<PerfCounterConfigData>();
        private bool isInstalling = false;
        private readonly object initializationLock = new object();
        private readonly Lazy<bool> isInitialized;
        private bool initializedGrainCounters;

        /// <summary>
        /// Default constructor
        /// </summary>
        public OrleansPerfCounterTelemetryConsumer(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<OrleansPerfCounterTelemetryConsumer>();
            this.isInitialized = new Lazy<bool>(this.Initialize, true);
            if (!AreWindowsPerfCountersAvailable(this.logger))
            {
                this.logger.Warn(ErrorCode.PerfCounterNotFound, "Windows perf counters not found -- defaulting to in-memory counters. " + ExplainHowToCreateOrleansPerfCounters);
            }
        }

        #region Counter Management methods

        /// <summary>
        /// Checks to see if windows perf counters as supported by OS.
        /// </summary>
        /// <returns></returns>
        public static bool AreWindowsPerfCountersAvailable(ILogger logger)
        {
            try
            {
                if (Environment.OSVersion.ToString().StartsWith("unix", StringComparison.InvariantCultureIgnoreCase))
                {
                    logger.Warn(ErrorCode.PerfCounterNotFound, "Windows perf counters are only available on Windows :) -- defaulting to in-memory counters.");
                    return false;
                }

                return PerformanceCounterCategory.Exists(CATEGORY_NAME);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.PerfCounterCategoryCheckError,
                    string.Format("Ignoring error checking for {0} perf counter category", CATEGORY_NAME), exc);
            }
            return false;
        }

        private PerformanceCounter CreatePerfCounter(string perfCounterName)
        {
            this.logger.Debug(ErrorCode.PerfCounterRegistering, "Creating perf counter {0}", perfCounterName);
            return new PerformanceCounter(CATEGORY_NAME, perfCounterName, false);
        }

        private static string GetPerfCounterName(PerfCounterConfigData cd)
        {
            return cd.Name.Name + "." + (cd.UseDeltaValue ? "Delta" : "Current");
        }

        /// <summary>
        /// Register Orleans perf counters with Windows
        /// </summary>
        /// <remarks>Note: Program needs to be running as Administrator to be able to delete Windows perf counters.</remarks>
        internal void InstallCounters()
        {
            if (PerformanceCounterCategory.Exists(CATEGORY_NAME))
                DeleteCounters();

            this.isInstalling = true;
            if (!this.isInitialized.Value)
            {
                var msg = "Unable to install Windows Performance counters";
                this.logger.Warn(ErrorCode.PerfCounterNotFound, msg);
                throw new InvalidOperationException(msg);
            }

            var collection = new CounterCreationDataCollection();

            foreach (PerfCounterConfigData cd in this.perfCounterData)
            {
                var perfCounterName = GetPerfCounterName(cd);
                var description = cd.Name.Name;

                var msg = string.Format("Registering perf counter {0}", perfCounterName);
                Console.WriteLine(msg);

                collection.Add(new CounterCreationData(perfCounterName, description, PerformanceCounterType.NumberOfItems32));
            }

            PerformanceCounterCategory.Create(
                CATEGORY_NAME,
                CATEGORY_DESCRIPTION,
                PerformanceCounterCategoryType.SingleInstance,
                collection);
        }

        /// <summary>
        /// Delete any existing perf counters registered with Windows
        /// </summary>
        /// <remarks>Note: Program needs to be running as Administrator to be able to delete Windows perf counters.</remarks>
        internal void DeleteCounters()
        {
            PerformanceCounterCategory.Delete(CATEGORY_NAME);
        }

        private PerfCounterConfigData GetCounter(string counterName)
        {
            return this.perfCounterData.Where(pcd => GetPerfCounterName(pcd) == counterName).SingleOrDefault();
        }

        #endregion

        #region IMetricTelemetryConsumer Methods

        /// <summary>
        /// Increment metric.
        /// </summary>
        /// <param name="name">metric name</param>
        public void IncrementMetric(string name) => WriteMetric(name, UpdateMode.Increment);

        /// <summary>
        /// Increment metric by value.
        /// </summary>
        /// <param name="name">metric name</param>
        /// <param name="value">metric value</param>
        public void IncrementMetric(string name, double value) => WriteMetric(name, UpdateMode.Increment, value);

        /// <summary>
        /// Track metric value
        /// </summary>
        /// <param name="name">metric name</param>
        /// <param name="value">metric value</param>
        /// <param name="properties">related properties</param>
        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null) => WriteMetric(name, UpdateMode.Set, value);

        /// <summary>
        /// Track metric value
        /// </summary>
        /// <param name="name">metric name</param>
        /// <param name="value">metric value</param>
        /// <param name="properties">related properties</param>
        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null) => WriteMetric(name, UpdateMode.Set, value.Ticks);

        /// <summary>
        /// Decrement metric
        /// </summary>
        /// <param name="name">metric name</param>
        public void DecrementMetric(string name) => WriteMetric(name, UpdateMode.Decrement);

        /// <summary>
        /// Decrement metric by value
        /// </summary>
        /// <param name="name">metric name</param>
        /// <param name="value">metric value</param>
        public void DecrementMetric(string name, double value) => WriteMetric(name, UpdateMode.Decrement, value);

        /// <summary>
        /// Write all pending metrics
        /// </summary>
        public void Flush() { }

        /// <summary>
        /// Close telemetry consumer
        /// </summary>
        public void Close() { }
        
        private bool Initialize()
        {
            try
            {
                // (1) Start with list of static counters
                var newPerfCounterData = new List<PerfCounterConfigData>(PerfCounterConfigData.StaticPerfCounters);

                // TODO: get rid of this static access. Telemetry consumers now allow being injected with dependencies, so extract it as such
				var grainTypes = CrashUtils.GrainTypes;                if (grainTypes != null)
                {
                    // (2) Then search for grain DLLs and pre-create activation counters for any grain types found
                    foreach (var grainType in grainTypes)
                    {
                        var counterName = new StatisticName(StatisticNames.GRAIN_COUNTS_PER_GRAIN, grainType);
                        newPerfCounterData.Add(new PerfCounterConfigData { Name = counterName, UseDeltaValue = false });
                    }

                    this.initializedGrainCounters = true;
                }

                if (!this.isInstalling)
                {
                    foreach (var cd in newPerfCounterData)
                    {
                        var perfCounterName = GetPerfCounterName(cd);
                        cd.PerfCounter = CreatePerfCounter(perfCounterName);
                    }
                }

                lock (this.initializationLock)
                {
                    this.perfCounterData.Clear();
                    this.perfCounterData.AddRange(newPerfCounterData);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void WriteMetric(string name, UpdateMode mode = UpdateMode.Increment, double? value = null)
        {
            if (!this.isInitialized.Value)
                return;

            // Attempt to initialize grain-specific counters if they haven't been initialized yet.
            if (!this.initializedGrainCounters)
            {
                this.Initialize();
            }

            PerfCounterConfigData cd = GetCounter(name);
            if (cd == null || cd.PerfCounter == null)
                return;

            StatisticName statsName = cd.Name;
            string perfCounterName = GetPerfCounterName(cd);

            try
            {

                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace(ErrorCode.PerfCounterWriting, "Writing perf counter {0}", perfCounterName);

                switch (mode)
                {
                    case UpdateMode.Increment:
                        if (value.HasValue)
                        {
                            cd.PerfCounter.IncrementBy((long)value.Value);
                        }
                        else
                        {
                            cd.PerfCounter.Increment();
                        }
                        break;
                    case UpdateMode.Decrement:
                        if (value.HasValue)
                        {
                            cd.PerfCounter.RawValue = cd.PerfCounter.RawValue - (long)value.Value;
                        }
                        else
                        {
                            cd.PerfCounter.Decrement();
                        }
                        break;
                    case UpdateMode.Set:
                        cd.PerfCounter.RawValue = (long)value.Value;
                        break;
                }
            }
            catch (Exception ex)
            {
                this.logger.Error(ErrorCode.PerfCounterUnableToWrite, string.Format("Unable to write to Windows perf counter '{0}'", statsName), ex);
            }
        }

        private enum UpdateMode
        {
            Increment = 0,
            Decrement,
            Set
        }

        #endregion
    }
}
