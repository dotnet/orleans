using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Storage;
using Orleans.Utilities;

namespace Orleans.Runtime
{
    public class PersistentStateFactory : IPersistentStateFactory
    {
        public IPersistentState<TState> Create<TState>(IGrainActivationContext context, IPersistentStateConfiguration cfg)
        {
            IGrainStorage storageProvider = !string.IsNullOrWhiteSpace(cfg.StorageName)
                ? context.ActivationServices.GetServiceByName<IGrainStorage>(cfg.StorageName)
                : context.ActivationServices.GetService<IGrainStorage>();
            if (storageProvider == null)
            {
                ThrowMissingProviderException(context, cfg);
            }
            string fullStateName = GetFullStateName(context, cfg);
            var bridge = new PersistentStateBridge<TState>(fullStateName, context, storageProvider);
            bridge.Participate(context.ObservableLifecycle);
            return bridge;
        }

        protected virtual string GetFullStateName(IGrainActivationContext context, IPersistentStateConfiguration cfg)
        {
            return $"{RuntimeTypeNameFormatter.Format(context.GrainType)}.{cfg.StateName}";
        }

        private static void ThrowMissingProviderException(IGrainActivationContext context, IPersistentStateConfiguration cfg)
        {
            string errMsg;
            var grainTypeName = context.GrainType.GetParseableName(TypeFormattingOptions.LogFormat);
            if (string.IsNullOrEmpty(cfg.StorageName))
            {
                errMsg = $"No default storage provider found loading grain type {grainTypeName}.";
            }
            else
            {
                errMsg = $"No storage provider named \"{cfg.StorageName}\" found loading grain type {grainTypeName}.";
            }

            throw new BadGrainStorageConfigException(errMsg);
        }

        private class PersistentStateBridge<TState> : IPersistentState<TState>, ILifecycleParticipant<IGrainLifecycle>
        {
            private readonly string fullStateName;
            private readonly IGrainActivationContext context;
            private readonly IGrainStorage storageProvider;
            private IStorage<TState> storage;

            public PersistentStateBridge(string fullStateName, IGrainActivationContext context, IGrainStorage storageProvider)
            {
                this.fullStateName = fullStateName;
                this.context = context;
                this.storageProvider = storageProvider;
            }

            public TState State
            {
                get { return this.storage.State; }
                set { this.storage.State = value; }
            }

            public string Etag => this.storage.Etag;

            public Task ClearStateAsync()
            {
                return this.storage.ClearStateAsync();
            }

            public Task ReadStateAsync()
            {
                return this.storage.ReadStateAsync();
            }

            public Task WriteStateAsync()
            {
                return this.storage.WriteStateAsync();
            }

            public void Participate(IGrainLifecycle lifecycle)
            {
                lifecycle.Subscribe(this.GetType().FullName, GrainLifecycleStage.SetupState, OnSetupState);
            }

            private Task OnSetupState(CancellationToken ct)
            {
                if (ct.IsCancellationRequested)
                    return Task.CompletedTask;
                this.storage = new StateStorageBridge<TState>(this.fullStateName, context.GrainInstance.GrainReference, this.storageProvider, context.ActivationServices.GetService<ILoggerFactory>());
                return this.ReadStateAsync();
            }
        }
    }
}
