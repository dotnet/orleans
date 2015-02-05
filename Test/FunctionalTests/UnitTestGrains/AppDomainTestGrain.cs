using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Host;
using Orleans.Runtime;

using UnitTestGrainInterfaces;
using UnitTests;

namespace UnitTestGrains
{

    internal class AppDomainTestGrain : Grain, IAppDomainTestGrain
    {
        private readonly Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private AppDomain appDomain;
        private ResultHandle result;

        public AppDomainTestGrain()
        {
            this.logger = GetLogger();
        }

        public override Task OnActivateAsync()
        {
            logger.Info("OnActivateAsync");

            try
            {
                this.watcher = ActivateDeactivateWatcherGrainFactory.GetGrain(0);

                // Create app domain
                string appDomainName = this.GetType().Name + "-" + this._Data.ActivationId;
                appDomain = AppDomain.CreateDomain(appDomainName);
                logger.Info("Created AppDomain {0}", appDomain.FriendlyName);

                // Load something into app domain
                result = (ResultHandle) appDomain.CreateInstanceFromAndUnwrap(
                    "Orleans.dll",
                    typeof (ResultHandle).FullName,
                    false,
                    BindingFlags.Default, null,
                    new object[] {},
                    CultureInfo.CurrentCulture,
                    new object[] {});
                result.Reset();
                logger.Info("Loaded {0} into AppDomain {1}", result, appDomain);

                return watcher.RecordActivateCall(this._Data.ActivationId);

            }
            catch (Exception exc)
            {
                logger.Error(0, "Exception during OnActivateAsync {0}", exc);
                throw;
            }
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");

            string appDomainName = appDomain.FriendlyName;
            AppDomain.Unload(appDomain);
            logger.Info("Unloaded AppDomain {0}", appDomainName);

            return watcher.RecordDeactivateCall(this._Data.ActivationId);
        }

        public Task<ActivationId> DoSomething()
        {
            logger.Info("DoSomething");
            result.Reset();
            return Task.FromResult(this._Data.ActivationId);
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            result.Reset();
            base.DeactivateOnIdle();
            return TaskDone.Done;
        }
    }

    internal class AppDomainHostTestGrain : Grain, IAppDomainHostTestGrain
    {
        private readonly Logger logger;

        private IActivateDeactivateWatcherGrain watcher;

        private static int portCounter = AppDomainHost.BaseServerPort;

        private Process process;
        private int port;

        private ResultHandle result;

        public AppDomainHostTestGrain()
        {
            this.logger = GetLogger();
        }

        public override Task OnActivateAsync()
        {
            logger.Info("OnActivateAsync");

            try
            {
                port = Interlocked.Increment(ref portCounter);

                this.watcher = ActivateDeactivateWatcherGrainFactory.GetGrain(0);

                // Init app domain hosting
                AppDomainHost.InitClient();

                // Spawn process to host remotable type in app domain
                result = (ResultHandle)AppDomainHost.GetRemoteObject(typeof(ResultHandle), port, null, out process);

                // Do something with hosted type
                result.Reset();

                return watcher.RecordActivateCall(this._Data.ActivationId);
            }
            catch (Exception exc)
            {
                logger.Error(0, "Exception during OnActivateAsync {0}", exc);
                AppDomainHost.KillHostProcess(process);
                process = null;
                throw;
            }

        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");

            AppDomainHost.KillHostProcess(process);
            result = null;
            process = null;
            port = 0;
        
            return watcher.RecordDeactivateCall(this._Data.ActivationId);
        }

        public Task<ActivationId> DoSomething()
        {
            logger.Info("DoSomething");
            result.Reset();
            return Task.FromResult(this._Data.ActivationId);
        }

        public Task DoDeactivate()
        {
            logger.Info("DoDeactivate");
            result.Reset();
            base.DeactivateOnIdle();
            return TaskDone.Done;
        }
    }
}
