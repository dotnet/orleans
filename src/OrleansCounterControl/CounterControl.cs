using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
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

        private static OrleansPerfCounterTelemetryConsumer perfCounterConsumer;

        public CounterControl()
        {
            // Check user is Administrator and has granted UAC elevation permission to run this app
            var userIdent = WindowsIdentity.GetCurrent();
            var userPrincipal = new WindowsPrincipal(userIdent);
            IsRunningAsAdministrator = userPrincipal.IsInRole(WindowsBuiltInRole.Administrator);
            
            var siloAssemblyLoader = new SiloAssemblyLoader(new NodeConfiguration());
            LogManager.GrainTypes = siloAssemblyLoader.GetGrainClassTypes(true).Keys.ToList();
            perfCounterConsumer = new OrleansPerfCounterTelemetryConsumer();
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
            
            InitConsoleLogging();

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
        private static void InitConsoleLogging()
        {
            Trace.Listeners.Clear();
            var cfg = new NodeConfiguration { TraceFilePattern = null, TraceToConsole = false };
            LogManager.Initialize(cfg);

            //TODO: Move it to use the APM APIs
            //var logWriter = new LogWriterToConsole(true, true); // Use compact console output & no timestamps / log message metadata
            //LogManager.LogConsumers.Add(logWriter);
        }

        /// <summary>
        /// Create the set of Orleans counters, if they do not already exist
        /// </summary>
        /// <param name="useBruteForce">Use brute force, if necessary</param>
        /// <remarks>Note: Program needs to be running as Administrator to be able to register Windows perf counters.</remarks>
        private static void RegisterWindowsPerfCounters(bool useBruteForce)
        {
            try
            {
                if (OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable())
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

                if (OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable())
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
        private static void UnregisterWindowsPerfCounters(bool useBruteForce)
        {
            if (!OrleansPerfCounterTelemetryConsumer.AreWindowsPerfCountersAvailable())
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
