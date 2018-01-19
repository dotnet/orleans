using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.GrainDirectory;
using Orleans.Providers;
using Orleans.MultiCluster;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Base class for all grains that use log-consistency for managing  the state.
    /// It is the equivalent of <see cref="Grain{T}"/> for grains using log-consistency.
    /// (SiloAssemblyLoader uses it to extract type)
    /// </summary>
    /// <typeparam name="TView">The type of the view</typeparam>
    public abstract class LogConsistentGrain<TView> : Grain, ILifecycleParticipant<IGrainLifecycle>

    {
        protected LogConsistentGrain()
        {
        }

        /// <summary>
        /// This constructor is particularly useful for unit testing where test code can create a Grain and replace
        /// the IGrainIdentity, IGrainRuntime and State with test doubles (mocks/stubs).
        /// </summary>
        protected LogConsistentGrain(IGrainIdentity identity, IGrainRuntime runtime)
            : base(identity, runtime)
        {
        }

        /// <summary>
        /// called right after grain construction to install the log view adaptor 
        /// </summary>
        /// <param name="factory"> The adaptor factory to use </param>
        /// <param name="state"> The initial state of the view </param>
        /// <param name="grainTypeName"> The type name of the grain </param>
        /// <param name="storageProvider"> The storage provider, if needed </param>
        /// <param name="services"> Protocol services </param>
        protected abstract void InstallAdaptor(ILogViewAdaptorFactory factory, object state, string grainTypeName, IStorageProvider storageProvider, ILogConsistencyProtocolServices services);

        /// <summary>
        /// Gets the default adaptor factory to use, or null if there is no default 
        /// (in which case user MUST configure a consistency provider)
        /// </summary>
        protected abstract ILogViewAdaptorFactory DefaultAdaptorFactory { get; }

        public override void Participate(IGrainLifecycle lifecycle)
        {
            base.Participate(lifecycle);
            lifecycle.Subscribe(GrainLifecycleStage.SetupState, OnSetupState);
            if(this is ILogConsistencyProtocolParticipant)
            {
                lifecycle.Subscribe(GrainLifecycleStage.Activate - 1, PreActivate);
                lifecycle.Subscribe(GrainLifecycleStage.Activate + 1, PostActivate);
            }
        }

        private Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.CompletedTask;
            IGrainActivationContext activationContext = this.ServiceProvider.GetRequiredService<IGrainActivationContext>();
            Factory<Grain, IMultiClusterRegistrationStrategy, ILogConsistencyProtocolServices> protocolServicesFactory = this.ServiceProvider.GetRequiredService<Factory<Grain, IMultiClusterRegistrationStrategy, ILogConsistencyProtocolServices>>();
            ILogViewAdaptorFactory consistencyProvider = SetupLogConsistencyProvider(activationContext);
            IStorageProvider storageProvider = consistencyProvider.UsesStorageProvider ? this.GetStorageProvider(this.ServiceProvider) : null;
            InstallLogViewAdaptor(activationContext.RegistrationStrategy, protocolServicesFactory, consistencyProvider, storageProvider);
            return Task.CompletedTask;
        }

        private async Task PreActivate(CancellationToken ct)
        {
            await ((ILogConsistencyProtocolParticipant)this).PreActivateProtocolParticipant();
        }

        private async Task PostActivate(CancellationToken ct)
        {
            await ((ILogConsistencyProtocolParticipant)this).PostActivateProtocolParticipant();
        }

        private void InstallLogViewAdaptor(
            IMultiClusterRegistrationStrategy mcRegistrationStrategy,
            Factory<Grain, IMultiClusterRegistrationStrategy, ILogConsistencyProtocolServices> protocolServicesFactory,
            ILogViewAdaptorFactory factory,
            IStorageProvider storageProvider)
        {
            // encapsulate runtime services used by consistency adaptors
            ILogConsistencyProtocolServices svc = protocolServicesFactory(this, mcRegistrationStrategy);

            TView state = (TView)Activator.CreateInstance(typeof(TView));

            this.InstallAdaptor(factory, state, this.GetType().FullName, storageProvider, svc);
        }


        private ILogViewAdaptorFactory SetupLogConsistencyProvider(IGrainActivationContext activationContext)
        {
            var attr = this.GetType().GetTypeInfo().GetCustomAttributes<LogConsistencyProviderAttribute>(true).FirstOrDefault();

            ILogViewAdaptorFactory defaultFactory = attr != null
                ? this.ServiceProvider.GetServiceByName<ILogConsistencyProvider>(attr.ProviderName)
                : this.ServiceProvider.GetService<ILogConsistencyProvider>();
            if (attr != null && defaultFactory == null)
            {
                var errMsg = attr != null
                    ? $"Cannot find consistency provider with Name={attr.ProviderName} for grain type {this.GetType().FullName}"
                    : $"No consistency provider manager found loading grain type {this.GetType().FullName}";
                throw new BadProviderConfigException(errMsg);
            }

            // use default if none found
            defaultFactory = defaultFactory ?? this.DefaultAdaptorFactory;
            if (defaultFactory == null)
            {
                var errMsg = $"No log consistency provider found loading grain type {this.GetType().FullName}";
                throw new BadProviderConfigException(errMsg);
            };

            return defaultFactory;
        }
    }
}
