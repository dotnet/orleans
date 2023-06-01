using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
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

        private sealed class PersistentStateBridge<TState> : StateStorageBridge<TState>, IPersistentState<TState>, ILifecycleParticipant<IGrainLifecycle>
        {
            public PersistentStateBridge(string stateName, IGrainContext context, IGrainStorage storageProvider) : base(stateName, context, storageProvider, context.ActivationServices.GetRequiredService<ILoggerFactory>())
            {
            }

            public void Participate(IGrainLifecycle lifecycle)
            {
                lifecycle.Subscribe(this.GetType().FullName, GrainLifecycleStage.SetupState, OnSetupState);
            }

            private Task OnSetupState(CancellationToken ct)
            {
                if (ct.IsCancellationRequested)
                {
                    return Task.CompletedTask;
                }

                // No need to load state if it has been loaded already via rehydration.
                if (IsStateInitialized)
                {
                    return Task.CompletedTask;
                }

                return this.ReadStateAsync();
            }
        }
    }
}
