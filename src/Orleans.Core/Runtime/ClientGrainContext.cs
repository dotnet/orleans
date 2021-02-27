using System;
using System.Collections.Concurrent;
using Orleans.Runtime;

namespace Orleans
{
    internal class ClientGrainContext : IGrainContext, IGrainExtensionBinder, IGrainContextAccessor
    {
        private readonly object _lockObj = new object();
        private readonly ConcurrentDictionary<Type, (object Implementation, IAddressable Reference)> _extensions = new ConcurrentDictionary<Type, (object, IAddressable)>();
        private readonly OutsideRuntimeClient _runtimeClient;
        private GrainReference _grainReference;

        public ClientGrainContext(OutsideRuntimeClient runtimeClient)
        {
            _runtimeClient = runtimeClient;
        }

        public GrainReference GrainReference => _grainReference ??= (GrainReference)_runtimeClient.InternalGrainFactory.GetGrain(this.GrainId);

        public GrainId GrainId => _runtimeClient.CurrentActivationAddress.Grain;

        public IAddressable GrainInstance => null;

        public ActivationId ActivationId => _runtimeClient.CurrentActivationAddress.Activation;

        public ActivationAddress Address => _runtimeClient.CurrentActivationAddress;

        public IServiceProvider ActivationServices => _runtimeClient.ServiceProvider;

        public IGrainLifecycle ObservableLifecycle => null;

        IGrainContext IGrainContextAccessor.GrainContext => this;

        public bool Equals(IGrainContext other) => ReferenceEquals(this, other);

        public TComponent GetComponent<TComponent>()
        {
            if (this is TComponent component) return component;
            return default;
        }

        public TTarget GetTarget<TTarget>()
        {
            if (this is TTarget target) return target;
            return default;
        }

        public void SetComponent<TComponent>(TComponent instance)
        {
            throw new NotSupportedException($"Cannot set components on shared client instance. Extension contract: {typeof(TComponent)}. Component: {instance} (Type: {instance?.GetType()})");
        }

        public (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension
        {
            (TExtension, TExtensionInterface) result;
            if (this.TryGetExtension(out result))
            {
                return result;
            }

            lock (_lockObj)
            {
                if (this.TryGetExtension(out result))
                {
                    return result;
                }

                var implementation = newExtensionFunc();
                var reference = _runtimeClient.InternalGrainFactory.CreateObjectReference<TExtensionInterface>(implementation);
                _extensions[typeof(TExtensionInterface)] = (implementation, reference);
                result = (implementation, reference);
                return result;
            }
        }

        private bool TryGetExtension<TExtension, TExtensionInterface>(out (TExtension, TExtensionInterface) result)
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension
        {
            if (_extensions.TryGetValue(typeof(TExtensionInterface), out var existing))
            {
                if (existing.Implementation is TExtension typedResult)
                {
                    result = (typedResult, existing.Reference.AsReference<TExtensionInterface>());
                    return true;
                }

                throw new InvalidCastException($"Cannot cast existing extension of type {existing.Implementation} to target type {typeof(TExtension)}");
            }

            result = default;
            return false;
        }

        private bool TryGetExtension<TExtensionInterface>(out TExtensionInterface result)
            where TExtensionInterface : IGrainExtension
        {
            if (_extensions.TryGetValue(typeof(TExtensionInterface), out var existing))
            {
                result = (TExtensionInterface)existing.Implementation;
                return true;
            }

            result = default;
            return false;
        }

        public TExtensionInterface GetExtension<TExtensionInterface>()
            where TExtensionInterface : IGrainExtension
        {
            if (this.TryGetExtension<TExtensionInterface>(out var result))
            {
                return result;
            }

            lock (_lockObj)
            {
                if (this.TryGetExtension(out result))
                {
                    return result;
                }

                var implementation = this.ActivationServices.GetServiceByKey<Type, IGrainExtension>(typeof(TExtensionInterface));
                if (implementation is null)
                {
                    throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
                }

                var reference = this.GrainReference.Cast<TExtensionInterface>();
                _extensions[typeof(TExtensionInterface)] = (implementation, reference);
                result = (TExtensionInterface)implementation;
                return result;
            }
        }

        public void ReceiveMessage(object message)
        {
            throw new NotImplementedException();
        }
    }
}