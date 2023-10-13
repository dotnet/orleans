using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
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

            var fullStateName = GetFullStateName(context, cfg);
            return new PersistentState<TState>(fullStateName, context, storageProvider);
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
    }

    internal sealed class PersistentState<TState> : StateStorageBridge<TState>, IPersistentState<TState>, ILifecycleObserver
    {
        public PersistentState(string stateName, IGrainContext context, IGrainStorage storageProvider) : base(stateName, context, storageProvider, context.ActivationServices.GetRequiredService<ILoggerFactory>(), context.ActivationServices.GetRequiredService<IActivatorProvider>())
        {
            var lifecycle = context.ObservableLifecycle;
            lifecycle.Subscribe(RuntimeTypeNameFormatter.Format(GetType()), GrainLifecycleStage.SetupState, this);
            lifecycle.AddMigrationParticipant(this);
        }

        public Task OnStart(CancellationToken cancellationToken = default) 
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            // No need to load state if it has been loaded already via rehydration.
            if (IsStateInitialized)
            {
                return Task.CompletedTask;
            }

            return ReadStateAsync();
        }

        public Task OnStop(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
