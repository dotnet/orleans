using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Collections;
using Orleans.Serialization;
using Orleans.Runtime.Configuration;

namespace OrleansTelemetryConsumers.Counters
{
    /// <summary>
    /// Providers installer hooks for registering Orleans custom performance counters.
    /// </summary>
    [RunInstaller(true)]
    public class OrleansPerformanceCounterInstaller : Installer
    {
        private OrleansPerfCounterTelemetryConsumer consumer;

        /// <summary>
        /// Constructors -- Registers Orleans system performance counters, 
        /// plus any grain-specific activation conters that can be detected when this installer is run.
        /// </summary>
        public OrleansPerformanceCounterInstaller()
        {
            SerializationManager.InitializeForTesting();
            Trace.Listeners.Clear();
            var cfg = new NodeConfiguration { TraceFilePattern = null, TraceToConsole = false };
            LogManager.Initialize(cfg);

            consumer = new OrleansPerfCounterTelemetryConsumer();

            if (GrainTypeManager.Instance == null)
            {
                var loader = new SiloAssemblyLoader(new Dictionary<string, SearchOption>());
                var typeManager = new GrainTypeManager(false, null, loader); // We shouldn't need GrainFactory in this case
                GrainTypeManager.Instance.Start(false);
            }
        }

        /// <summary>
        /// Installs predefined performance counters logged by this telemetry consumer.
        /// </summary>
        /// <param name="stateSaver">An <see cref="T:System.Collections.IDictionary" /> used to save information needed to perform a commit, rollback, or uninstall operation. </param>
        public override void Install(IDictionary stateSaver)
        {
            consumer.InstallCounters();
            if (OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable())
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
            if (!OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable())
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
