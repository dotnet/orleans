using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
#if NETSTANDARD
    using AppDomain = System.AppDomain;
#endif

    /// <summary>
    /// Represents a handle to a silo that is deployed inside a remote AppDomain, but in the same process
    /// </summary>
    public class AppDomainSiloHandle : SiloHandle
    {
        private bool isActive = true;

        private Dictionary<string, GeneratedAssembly> additionalAssemblies;

        /// <summary> Get or set the AppDomain used by the silo </summary>
        public AppDomain AppDomain { get; set; }

        /// <summary>Gets or sets a reference to the silo host that is marshable by reference.</summary>
        public AppDomainSiloHost SiloHost { get; set; }

        /// <inheritdoc />
        public override bool IsActive => isActive;

        /// <summary>Creates a new silo in a remote app domain and returns a handle to it.</summary>
        public static SiloHandle Create(string siloName, Silo.SiloType type, ClusterConfiguration config, NodeConfiguration nodeConfiguration, Dictionary<string, GeneratedAssembly> additionalAssemblies)
        {
            AppDomainSetup setup = GetAppDomainSetupInfo();

            var appDomain = AppDomain.CreateDomain(siloName, null, setup);

            try
            {
                // Load each of the additional assemblies.
                AppDomainSiloHost.CodeGeneratorOptimizer optimizer = null;
                foreach (var assembly in additionalAssemblies.Where(asm => asm.Value != null))
                {
                    if (optimizer == null)
                    {
                        optimizer =
                            (AppDomainSiloHost.CodeGeneratorOptimizer)
                            appDomain.CreateInstanceAndUnwrap(
                                typeof(AppDomainSiloHost.CodeGeneratorOptimizer).Assembly.FullName, typeof(AppDomainSiloHost.CodeGeneratorOptimizer).FullName, false,
                                BindingFlags.Default,
                                null,
                                null,
                                CultureInfo.CurrentCulture,
                                new object[] { });
                    }

                    optimizer.AddCachedAssembly(assembly.Key, assembly.Value);
                }

                var args = new object[] { siloName, type, config };

                var siloHost = (AppDomainSiloHost)appDomain.CreateInstanceAndUnwrap(
                    typeof(AppDomainSiloHost).Assembly.FullName, typeof(AppDomainSiloHost).FullName, false,
                    BindingFlags.Default, null, args, CultureInfo.CurrentCulture,
                    new object[] { });

                appDomain.UnhandledException += ReportUnobservedException;

                siloHost.Start();

                var retValue = new AppDomainSiloHandle
                {
                    Name = siloName,
                    SiloHost = siloHost,
                    NodeConfiguration = nodeConfiguration,
                    SiloAddress = siloHost.SiloAddress,
                    Type = type,
                    AppDomain = appDomain,
                    additionalAssemblies = additionalAssemblies,
#if !NETSTANDARD_TODO
                    AppDomainTestHook = siloHost.AppDomainTestHook,
#endif
                };

                retValue.ImportGeneratedAssemblies();

                return retValue;
            }
            catch (Exception)
            {
                UnloadAppDomain(appDomain);
                throw;
            }
        }


        private Dictionary<string, GeneratedAssembly> TryGetGeneratedAssemblies()
        {
            var tryToRetrieveGeneratedAssemblies = Task.Run(() =>
            {
                try
                {
                    if (this.SiloHost != null)
                    {
                        var generatedAssemblies = new AppDomainSiloHost.GeneratedAssemblies();
                        this.SiloHost.UpdateGeneratedAssemblies(generatedAssemblies);

                        return generatedAssemblies.Assemblies;
                    }
                }
                catch (Exception exc)
                {
                    WriteLog($"UpdateGeneratedAssemblies threw an exception. Ignoring it. Exception: {exc}");
                }

                return null;
            });

            // best effort to try to import generated assemblies, otherwise move on.
            if (tryToRetrieveGeneratedAssemblies.Wait(TimeSpan.FromSeconds(3)))
            {
                return tryToRetrieveGeneratedAssemblies.Result;
            }

            return null;
        }

        /// <inheritdoc />
        public override void StopSilo(bool stopGracefully)
        {
            if (!IsActive) return;

            if (stopGracefully)
            {
                try
                {
                    this.SiloHost.Shutdown();
                }
                catch (RemotingException re)
                {
                    WriteLog(re); /* Ignore error */
                }
                catch (Exception exc)
                {
                    WriteLog(exc);
                    throw;
                }
            }

            ImportGeneratedAssemblies();

            this.isActive = false;
            try
            {
                UnloadAppDomain();
            }
            catch (Exception exc)
            {
                WriteLog(exc);
                throw;
            }

            this.SiloHost = null;
        }

        private void UnloadAppDomain()
        {
            UnloadAppDomain(this.AppDomain);
            this.AppDomain = null;
        }

        private static void UnloadAppDomain(AppDomain appDomain)
        {
            if (appDomain != null)
            {
                appDomain.UnhandledException -= ReportUnobservedException;
                AppDomain.Unload(appDomain);
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (!this.IsActive) return;

            if (disposing)
            {
                StopSilo(true);
            }
            else
            {
                // Do not attempt to unload the AppDomain in the finalizer thread itself, as it is not supported, and also not a lot of work should be done in it
                var appDomain = this.AppDomain;
                appDomain.UnhandledException -= ReportUnobservedException;
                Task.Run(() => AppDomain.Unload(appDomain)).Ignore();
            }
        }

        /// <summary>
        /// Imports assemblies generated by runtime code generation from the provided silo.
        /// </summary>
        private void ImportGeneratedAssemblies()
        {
            var generatedAssemblies = this.TryGetGeneratedAssemblies();
            if (generatedAssemblies != null)
            {
                foreach (var assembly in generatedAssemblies)
                {
                    // If we have never seen generated code for this assembly before, or generated code might be
                    // newer, store it for later silo creation.
                    GeneratedAssembly existing;
                    if (!additionalAssemblies.TryGetValue(assembly.Key, out existing) || assembly.Value != null)
                    {
                        additionalAssemblies[assembly.Key] = assembly.Value;
                    }
                }
            }
        }

        private void WriteLog(object value)
        {
            // TODO: replace
            Console.WriteLine(value.ToString());
        }

        internal static AppDomainSetup GetAppDomainSetupInfo()
        {
            var currentAppDomain = AppDomain.CurrentDomain;

            return new AppDomainSetup
            {
                ApplicationBase = Environment.CurrentDirectory,
                ConfigurationFile = currentAppDomain.SetupInformation.ConfigurationFile,
                ShadowCopyFiles = currentAppDomain.SetupInformation.ShadowCopyFiles,
                ShadowCopyDirectories = currentAppDomain.SetupInformation.ShadowCopyDirectories,
                CachePath = currentAppDomain.SetupInformation.CachePath
            };
        }

        private static void ReportUnobservedException(object sender, System.UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = (Exception)eventArgs.ExceptionObject;
            // WriteLog("Unobserved exception: {0}", exception);
        }
    }
}