using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Remoting;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Represents a handle to a silo that is deployed inside a remote AppDomain, but in the same process
    /// </summary>
    public class AppDomainSiloHandle : SiloHandle
    {
        private bool isActive = true;
        
        /// <summary> Get or set the AppDomain used by the silo </summary>
        public AppDomain AppDomain { get; set; }

        /// <summary>Gets or sets a reference to the silo host that is marshable by reference.</summary>
        public AppDomainSiloHost SiloHost { get; set; }

        /// <inheritdoc />
        public override bool IsActive => isActive;

        /// <summary>Creates a new silo in a remote app domain and returns a handle to it.</summary>
        public static SiloHandle Create(
            string siloName,
            Silo.SiloType type,
            Type siloBuilderFactory,
            ClusterConfiguration config,
            NodeConfiguration nodeConfiguration,
            string applicationBase = null)
        {
            AppDomainSetup setup = GetAppDomainSetupInfo(applicationBase);

            var appDomain = AppDomain.CreateDomain(siloName, null, setup);
            
            try
            {
                var args = new object[] {siloName, siloBuilderFactory, config};

                var siloHost = (AppDomainSiloHost) appDomain.CreateInstanceAndUnwrap(
                    typeof(AppDomainSiloHost).Assembly.FullName,
                    typeof(AppDomainSiloHost).FullName,
                    false,
                    BindingFlags.Default,
                    null,
                    args,
                    CultureInfo.CurrentCulture,
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
                    AppDomainTestHook = siloHost.AppDomainTestHook,
                };

                return retValue;
            }
            catch (Exception)
            {
                UnloadAppDomain(appDomain);
                throw;
            }
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

        private void WriteLog(object value)
        {
            // TODO: replace
            Console.WriteLine(value.ToString());
        }

        internal static AppDomainSetup GetAppDomainSetupInfo(string applicationBase)
        {
            var currentAppDomain = AppDomain.CurrentDomain;

            return new AppDomainSetup
            {
                ApplicationBase = string.IsNullOrEmpty(applicationBase) ? Environment.CurrentDirectory : applicationBase,
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