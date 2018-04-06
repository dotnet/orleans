using Orleans.Runtime;
using System;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Logging;
using Orleans.Metadata;
using Orleans.Runtime.Configuration;

namespace OrleansTelemetryConsumers.Counters
{
    /// <summary>
    /// Providers installer hooks for registering Orleans custom performance counters.
    /// </summary>
    [RunInstaller(true)]
    public class OrleansPerformanceCounterInstaller : Installer
    {
        private readonly OrleansPerfCounterTelemetryConsumer consumer;
        private readonly ILogger logger;
        /// <summary>
        /// Constructors -- Registers Orleans system performance counters, 
        /// plus any grain-specific activation counters that can be detected when this installer is run.
        /// </summary>
        public OrleansPerformanceCounterInstaller()
        {
            Trace.Listeners.Clear();
            var loggerFactory = CreateDefaultLoggerFactory($"{this.GetType()}.log");

            var parts = new ApplicationPartManager();
            parts.ConfigureDefaults().AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainClassFeature>());
            var grainClassFeature = parts.CreateAndPopulateFeature<GrainClassFeature>();

            CrashUtils.GrainTypes = grainClassFeature.Classes.Select(metadata => TypeUtils.GetFullName(metadata.ClassType)).ToList();
            consumer = new OrleansPerfCounterTelemetryConsumer(loggerFactory);
            this.logger = loggerFactory.CreateLogger<OrleansPerformanceCounterInstaller>();
        }

        private static ILoggerFactory CreateDefaultLoggerFactory(string filePath)
        {
            var factory = new LoggerFactory();
            factory.AddProvider(new FileLoggerProvider(filePath));
            if (ConsoleText.IsConsoleAvailable)
                factory.AddConsole();
            return factory;
        }

        /// <summary>
        /// Installs predefined performance counters logged by this telemetry consumer.
        /// </summary>
        /// <param name="stateSaver">An <see cref="T:System.Collections.IDictionary" /> used to save information needed to perform a commit, rollback, or uninstall operation. </param>
        public override void Install(IDictionary stateSaver)
        {
            consumer.InstallCounters();
            if (OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable(this.logger))
                Context.LogMessage("Orleans counters registered successfully");
            else
                Context.LogMessage("Orleans counters are NOT registered");

            base.Install(stateSaver);
        }

        /// <summary>
        /// Removes performance counters installed by this telemetry consumers.
        /// </summary>
        /// <param name="savedState">An <see cref="T:System.Collections.IDictionary" /> that contains the state of the computer after the installation was complete. </param>
        public override void Uninstall(IDictionary savedState)
        {
            if (!OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable(this.logger))
            {
                Context.LogMessage("Orleans counters are already unregistered");
            }
            else
            {
                try
                {
                    consumer.DeleteCounters();
                }
                catch (Exception exc)
                {
                    Context.LogMessage("Error deleting old Orleans counters - {0}" + exc);
                }
            }

            base.Uninstall(savedState);
        }
    }
}
