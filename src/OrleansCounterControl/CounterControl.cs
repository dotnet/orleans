using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Logging;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using OrleansTelemetryConsumers.Counters;

namespace Orleans.Counter.Control
{
    /// <summary>
    /// Control Orleans Counters - Register or Unregister the Orleans counter set
    /// </summary>
    internal class CounterControl
    {
        public bool Unregister { get; private set; }
        public bool BruteForce { get; private set; }
        public bool NeedRunAsAdministrator { get; private set; }
        public bool IsRunningAsAdministrator { get; private set; }
        public bool PauseAtEnd { get; private set; }
        private readonly ILogger logger;
        private static OrleansPerfCounterTelemetryConsumer perfCounterConsumer;

        public CounterControl(ILoggerFactory loggerFactory)
        {
            // Check user is Administrator and has granted UAC elevation permission to run this app
            var userIdent = WindowsIdentity.GetCurrent();
            var userPrincipal = new WindowsPrincipal(userIdent);
            IsRunningAsAdministrator = userPrincipal.IsInRole(WindowsBuiltInRole.Administrator);

            var parts = new ApplicationPartManager();
            parts.ConfigureDefaults()
                .AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainClassFeature>());
            var grainClassFeature = parts.CreateAndPopulateFeature<GrainClassFeature>();

            CrashUtils.GrainTypes = grainClassFeature.Classes.Select(metadata => TypeUtils.GetFullName(metadata.ClassType)).ToList();

            perfCounterConsumer = new OrleansPerfCounterTelemetryConsumer(loggerFactory);
            this.logger = loggerFactory.CreateLogger<CounterControl>();
        }

        public void PrintUsage()
        {
            using (var usageStr = new StringWriter())
            {
                usageStr.WriteLine(typeof(CounterControl).GetTypeInfo().Assembly.GetName().Name + ".exe {command}");
                usageStr.WriteLine("Where commands are:");
                usageStr.WriteLine(" /? or /help       = Display usage info");
                usageStr.WriteLine(" /r or /register   = Register Windows performance counters for Orleans [default]");
                usageStr.WriteLine(" /u or /unregister = Unregister Windows performance counters for Orleans");
                usageStr.WriteLine(" /f or /force      = Use brute force, if necessary");
                usageStr.WriteLine(" /pause            = Pause for user key press after operation");

                ConsoleText.WriteUsage(usageStr.ToString());
            }
        }

        public bool ParseArguments(string[] args)
        {
            bool ok = true;
            NeedRunAsAdministrator = true;
            Unregister = false;

            foreach (var arg in args)
            {
                if (arg.StartsWith("/") || arg.StartsWith("-"))
                {
                    var a = arg.ToLowerInvariant().Substring(1);
                    switch (a)
                    {
                        case "r":
                        case "register":
                            Unregister = false;
                            break;
                        case "u":
                        case "unregister":
                            Unregister = true;
                            break;
                        case "f":
                        case "force":
                            BruteForce = true;
                            break;
                        case "pause":
                            PauseAtEnd = true;
                            break;
                        case "?":
                        case "help":
                            NeedRunAsAdministrator = false;
                            ok = false;
                            break;
                        default:
                            NeedRunAsAdministrator = false;
                            ok = false;
                            break;
                    }
                }
                else
                {
                    ConsoleText.WriteError("Unrecognised command line option: " + arg);
                    ok = false;
                }
            }

            return ok;
        }

        public int Run()
        {
            if (NeedRunAsAdministrator && !IsRunningAsAdministrator)
            {
                ConsoleText.WriteError("Need to be running in Administrator role to perform the requested operations.");
                return 1;
            }

            try
            {
                if (Unregister)
                {
                    ConsoleText.WriteStatus("Unregistering Orleans performance counters with Windows");
                    UnregisterWindowsPerfCounters(this.BruteForce);
                }
                else
                {
                    ConsoleText.WriteStatus("Registering Orleans performance counters with Windows");
                    RegisterWindowsPerfCounters(true); // Always reinitialize counter registrations, even if already existed
                }

                ConsoleText.WriteStatus("Operation completed successfully.");
                return 0;
            }
            catch (Exception exc)
            {
                ConsoleText.WriteError("Error running " + typeof(CounterControl).GetTypeInfo().Assembly.GetName().Name + ".exe", exc);

                if (!BruteForce) return 2;

                ConsoleText.WriteStatus("Ignoring error due to brute-force mode");
                return 0;
            }
        }

        /// <summary>
        /// Initialize log infrastructure for Orleans runtime sub-components
        /// </summary>
        internal static ILoggerFactory InitDefaultLogging()
        {
            Trace.Listeners.Clear();
            return CreateDefaultLoggerFactory("CounterControl.log");
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
        /// Create the set of Orleans counters, if they do not already exist
        /// </summary>
        /// <param name="useBruteForce">Use brute force, if necessary</param>
        /// <remarks>Note: Program needs to be running as Administrator to be able to register Windows perf counters.</remarks>
        private void RegisterWindowsPerfCounters(bool useBruteForce)
        {
            try
            {
                if (OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable(this.logger))
                {
                    if (!useBruteForce)
                    {
                        ConsoleText.WriteStatus("Orleans counters are already registered -- Use brute-force mode to re-initialize");
                        return;
                    }

                    // Delete any old perf counters
                    UnregisterWindowsPerfCounters(true);
                }

                // Register perf counters
                perfCounterConsumer.InstallCounters();

                if (OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable(this.logger))
                    ConsoleText.WriteStatus("Orleans counters registered successfully");
                else
                    ConsoleText.WriteError("Orleans counters are NOT registered");
            }
            catch (Exception exc)
            {
                ConsoleText.WriteError("Error registering Orleans counters - {0}" + exc);
                throw;
            }
        }

        /// <summary>
        /// Remove the set of Orleans counters, if they already exist
        /// </summary>
        /// <param name="useBruteForce">Use brute force, if necessary</param>
        /// <remarks>Note: Program needs to be running as Administrator to be able to unregister Windows perf counters.</remarks>
        private void UnregisterWindowsPerfCounters(bool useBruteForce)
        {
            if (!OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable(this.logger))
            {
                ConsoleText.WriteStatus("Orleans counters are already unregistered");
                return;
            }

            // Delete any old perf counters
            try
            {
                perfCounterConsumer.DeleteCounters();
            }
            catch (Exception exc)
            {
                ConsoleText.WriteError("Error deleting old Orleans counters - {0}" + exc);
                if (useBruteForce)
                    ConsoleText.WriteStatus("Ignoring error deleting Orleans counters due to brute-force mode");
                else
                    throw;
            }
        }
    }
}
