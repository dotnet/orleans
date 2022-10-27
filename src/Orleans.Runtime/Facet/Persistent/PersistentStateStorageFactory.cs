using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Storage;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Creates <see cref="IPersistentState{TState}"/> instances for grains.
    /// </summary>
    /// <seealso cref="Orleans.Runtime.IPersistentStateFactory" />
    public class PersistentStateFactory : IPersistentStateFactory
    {

        /// <inheritdoc/>
        public IPersistentState<TState> Create<TState>(IGrainContext context, IPersistentStateConfiguration cfg)
        {
            var storageProvider = !string.IsNullOrWhiteSpace(cfg.StorageName)
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

        protected virtual string GetFullStateName(IGrainContext context, IPersistentStateConfiguration cfg)
        {
            return cfg.StateName;
        }

        [DoesNotReturn]
        private static void ThrowMissingProviderException(IGrainContext context, IPersistentStateConfiguration cfg)
        {
            string errMsg;
            if (string.IsNullOrEmpty(cfg.StorageName))
            {
                errMsg = $"No default storage provider found loading grain type {context.GrainId.Type}.";
            }
            else
            {
                errMsg = $"No storage provider named \"{cfg.StorageName}\" found loading grain type {context.GrainId.Type}.";
            }

            throw new BadProviderConfigException(errMsg);
        }

        private sealed class PersistentStateBridge<TState> : IPersistentState<TState>, ILifecycleParticipant<IGrainLifecycle>
        {
            private readonly string stateName;
            private readonly IGrainContext context;
            private readonly IGrainStorage storageProvider;
            private StateStorageBridge<TState> storage;

            public PersistentStateBridge(string stateName, IGrainContext context, IGrainStorage storageProvider)
            {
                this.stateName = stateName;
                this.context = context;
                this.storageProvider = storageProvider;
                storage = null!;
            }

            public TState State
            {
                get { return this.storage.State; }
                set { this.storage.State = value; }
            }

            public string Etag => this.storage.Etag;

            public bool RecordExists => this.storage.RecordExists;

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
                storage = new(stateName, context.GrainId, storageProvider, context.ActivationServices.GetRequiredService<ILoggerFactory>());
                return this.ReadStateAsync();
            }
        }
    }
}
