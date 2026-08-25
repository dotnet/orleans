using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Diagnostics;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime;

internal sealed partial class ActivationData
{
    public object? GetTarget() => GrainInstance;

    object? ITargetHolder.GetComponent(Type componentType)
    {
        var result = GetComponent(componentType);
        if (result is null && typeof(IGrainExtension).IsAssignableFrom(componentType))
        {
            var implementation = ActivationServices.GetKeyedService<IGrainExtension>(componentType);
            if (implementation is not { } typedResult)
            {
                throw new GrainExtensionNotInstalledException($"No extension of type {componentType} is installed on this instance and no implementations are registered for automated install");
            }

            SetComponent(componentType, typedResult);
            result = typedResult;
        }

        return result;
    }

    public TComponent? GetComponent<TComponent>() where TComponent : class => GetComponent(typeof(TComponent)) as TComponent;

    public object? GetComponent(Type componentType)
    {
        object? result;
        if (componentType.IsAssignableFrom(GrainInstance?.GetType()))
        {
            result = GrainInstance;
        }
        else if (componentType.IsAssignableFrom(GetType()))
        {
            result = this;
        }
        else if (_extras is { } extras && extras.TryGetComponent(componentType, out var resultObj))
        {
            result = resultObj;
        }
        else if (_shared.GetComponent(componentType) is { } sharedComponent)
        {
            result = sharedComponent;
        }
        else if (ActivationServices.GetService(componentType) is { } component)
        {
            SetComponent(componentType, component);
            result = component;
        }
        else
        {
            result = null;
        }

        return result;
    }

    public void SetComponent<TComponent>(TComponent? instance) where TComponent : class => SetComponent(typeof(TComponent), instance);

    public void SetComponent(Type componentType, object? instance)
    {
        if (componentType.IsAssignableFrom(GrainInstance?.GetType()))
        {
            throw new ArgumentException("Cannot override a component which is implemented by this grain");
        }
        else if (componentType.IsAssignableFrom(GetType()))
        {
            throw new ArgumentException("Cannot override a component which is implemented by this grain context");
        }

        lock (_lock)
        {
            if (instance == null)
            {
                if (componentType == typeof(GrainCanInterleave) && _extras is { } existingExtras)
                {
                    existingExtras.InterleavingPredicate = null;
                }

                _extras?.RemoveComponent(componentType);
                return;
            }

            _extras ??= new();
            if (componentType == typeof(GrainCanInterleave))
            {
                _extras.InterleavingPredicate = (GrainCanInterleave)instance;
            }

            _extras.SetComponent(componentType, instance);
        }
    }

    public (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
        where TExtension : class, TExtensionInterface
        where TExtensionInterface : class, IGrainExtension
    {
        TExtension implementation;
        if (GetComponent<TExtensionInterface>() is object existing)
        {
            if (existing is TExtension typedResult)
            {
                implementation = typedResult;
            }
            else
            {
                throw new InvalidCastException($"Cannot cast existing extension of type {existing.GetType()} to target type {typeof(TExtension)}");
            }
        }
        else
        {
            implementation = newExtensionFunc();
            SetComponent<TExtensionInterface>(implementation);
        }

        var reference = GrainReference.Cast<TExtensionInterface>();
        return (implementation, reference);
    }

    public TExtensionInterface GetExtension<TExtensionInterface>()
        where TExtensionInterface : class, IGrainExtension
    {
        if (GetComponent<TExtensionInterface>() is TExtensionInterface result)
        {
            return result;
        }

        var implementation = ActivationServices.GetKeyedService<IGrainExtension>(typeof(TExtensionInterface));
        if (implementation is not TExtensionInterface typedResult)
        {
            throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
        }

        SetComponent(typedResult);
        return typedResult;
    }


    void ICallChainReentrantGrainContext.OnEnterReentrantSection(Guid reentrancyId)
    {
        var tracker = (_extras ??= new()).ReentrantRequests ??= new();
        tracker.EnterReentrantSection(reentrancyId);
    }

    void ICallChainReentrantGrainContext.OnExitReentrantSection(Guid reentrancyId)
    {
        var tracker = _extras?.ReentrantRequests;
        if (tracker is null)
        {
            throw new InvalidOperationException("Attempted to exit reentrant section without entering it.");
        }

        tracker.LeaveReentrantSection(reentrancyId);
    }

    private bool IsReentrantSection(Guid reentrancyId)
    {
        if (reentrancyId == Guid.Empty)
        {
            return false;
        }

        var tracker = _extras?.ReentrantRequests;
        if (tracker is null)
        {
            return false;
        }

        return tracker.IsReentrantSectionActive(reentrancyId);
    }

    internal class ReentrantRequestTracker : Dictionary<Guid, int>
    {
        public void EnterReentrantSection(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(this, reentrancyId, out _);
            ++count;
        }

        public void LeaveReentrantSection(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(this, reentrancyId);
            if (Unsafe.IsNullRef(ref count))
            {
                return;
            }

            if (--count <= 0)
            {
                Remove(reentrancyId);
            }
        }

        public bool IsReentrantSectionActive(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            return TryGetValue(reentrancyId, out var count) && count > 0;
        }
    }
}
