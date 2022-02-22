using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Providers;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// Base class for all grains that use log-consistency for managing  the state.
    /// It is the equivalent of <see cref="Grain{T}"/> for grains using log-consistency.
    /// (SiloAssemblyLoader uses it to extract type)
    /// </summary>
    /// <typeparam name="TView">The type of the view</typeparam>
    public abstract class LogConsistentGrain<TView> : Grain, ILifecycleParticipant<IGrainLifecycle>
    {
        /// <summary>
        /// called right after grain construction to install the log view adaptor
        /// </summary>
        /// <param name="factory"> The adaptor factory to use </param>
        /// <param name="state"> The initial state of the view </param>
        /// <param name="grainTypeName"> The type name of the grain </param>
        /// <param name="grainStorage"> The grain storage, if needed </param>
        /// <param name="services"> Protocol services </param>
        protected abstract void InstallAdaptor(ILogViewAdaptorFactory factory, object state, string grainTypeName, IGrainStorage grainStorage, ILogConsistencyProtocolServices services);

        /// <summary>
        /// Gets the default adaptor factory to use, or null if there is no default
        /// (in which case user MUST configure a consistency provider)
        /// </summary>
        protected abstract ILogViewAdaptorFactory DefaultAdaptorFactory { get; }

        public virtual void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<LogConsistentGrain<TView>>(GrainLifecycleStage.SetupState, OnSetupState, OnDeactivateState);
            if (this is ILogConsistencyProtocolParticipant)
            {
                lifecycle.Subscribe<LogConsistentGrain<TView>>(GrainLifecycleStage.Activate - 1, PreActivate);
                lifecycle.Subscribe<LogConsistentGrain<TView>>(GrainLifecycleStage.Activate + 1, PostActivate);
            }
        }

        private async Task OnDeactivateState(CancellationToken ct)
        {
            if (this is ILogConsistencyProtocolParticipant participant)
            {
                await participant.DeactivateProtocolParticipant();
            }
        }

        private Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.CompletedTask;
            IGrainContextAccessor grainContextAccessor = this.ServiceProvider.GetRequiredService<IGrainContextAccessor>();
            Factory<IGrainContext, ILogConsistencyProtocolServices> protocolServicesFactory = this.ServiceProvider.GetRequiredService<Factory<IGrainContext, ILogConsistencyProtocolServices>>();
            var grainContext = grainContextAccessor.GrainContext;
            ILogViewAdaptorFactory consistencyProvider = SetupLogConsistencyProvider(grainContext);
            IGrainStorage grainStorage = consistencyProvider.UsesStorageProvider ? GrainStorageHelpers.GetGrainStorage(grainContext?.GrainInstance.GetType(), this.ServiceProvider) : null;
            InstallLogViewAdaptor(grainContext, protocolServicesFactory, consistencyProvider, grainStorage);
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
            IGrainContext grainContext,
            Factory<IGrainContext, ILogConsistencyProtocolServices> protocolServicesFactory,
            ILogViewAdaptorFactory factory,
            IGrainStorage grainStorage)
        {
            // encapsulate runtime services used by consistency adaptors
            ILogConsistencyProtocolServices svc = protocolServicesFactory(grainContext);

            TView state = (TView)Activator.CreateInstance(typeof(TView));

            this.InstallAdaptor(factory, state, this.GetType().FullName, grainStorage, svc);
        }

        private ILogViewAdaptorFactory SetupLogConsistencyProvider(IGrainContext activationContext)
        {
            var attr = this.GetType().GetCustomAttributes<LogConsistencyProviderAttribute>(true).FirstOrDefault();

            ILogViewAdaptorFactory defaultFactory = attr != null
                ? this.ServiceProvider.GetServiceByName<ILogViewAdaptorFactory>(attr.ProviderName)
                : this.ServiceProvider.GetService<ILogViewAdaptorFactory>();
            if (attr != null && defaultFactory == null)
            {
                var errMsg = $"Cannot find consistency provider with Name={attr.ProviderName} for grain type {this.GetType().FullName}";
                throw new BadGrainStorageConfigException(errMsg);
            }

            // use default if none found
            defaultFactory = defaultFactory ?? this.DefaultAdaptorFactory;
            if (defaultFactory == null)
            {
                var errMsg = $"No log consistency provider found loading grain type {this.GetType().FullName}";
                throw new BadGrainStorageConfigException(errMsg);
            };

            return defaultFactory;
        }
    }
}
