using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Configures a fault-injecting transactional state facet.
    /// </summary>
    public interface IFaultInjectionTransactionalStateConfiguration : ITransactionalStateConfiguration
    {
    }

    /// <summary>
    /// Identifies a constructor parameter as fault-injecting transactional state.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class FaultInjectionTransactionalStateAttribute : Attribute, IFacetMetadata, IFaultInjectionTransactionalStateConfiguration
    {
        /// <summary>
        /// Gets the state name.
        /// </summary>
        public string StateName { get; }

        /// <summary>
        /// Gets the storage provider name.
        /// </summary>
        public string? StorageName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultInjectionTransactionalStateAttribute"/> class.
        /// </summary>
        /// <param name="stateName">The state name.</param>
        /// <param name="storageName">The storage provider name, or <see langword="null"/> to use the default provider.</param>
        public FaultInjectionTransactionalStateAttribute(string stateName, string? storageName = null)
        {
            this.StateName = stateName;
            this.StorageName = storageName;
        }
    }

    /// <summary>
    /// Creates fault-injecting transactional state facets.
    /// </summary>
    public interface IFaultInjectionTransactionalStateFactory
    {
        /// <summary>
        /// Creates a fault-injecting transactional state facet.
        /// </summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <param name="config">The transactional state configuration.</param>
        /// <returns>The fault-injecting transactional state facet.</returns>
        IFaultInjectionTransactionalState<TState> Create<TState>(IFaultInjectionTransactionalStateConfiguration config) where TState : class, new();
    }

    /// <summary>
    /// Creates fault-injecting transactional state facets.
    /// </summary>
    public class FaultInjectionTransactionalStateFactory : IFaultInjectionTransactionalStateFactory
    {
        private readonly IGrainContextAccessor contextAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultInjectionTransactionalStateFactory"/> class.
        /// </summary>
        /// <param name="contextAccessor">The accessor for the current grain context.</param>
        public FaultInjectionTransactionalStateFactory(IGrainContextAccessor contextAccessor)
        {
            this.contextAccessor = contextAccessor;
        }

        /// <inheritdoc />
        public IFaultInjectionTransactionalState<TState> Create<TState>(IFaultInjectionTransactionalStateConfiguration config) where TState : class, new()
        {
            var currentContext = this.contextAccessor.GrainContext;
            TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(currentContext.ActivationServices, new TransactionalStateConfiguration(config), this.contextAccessor);
            FaultInjectionTransactionalState<TState> deactivationTransactionalState = ActivatorUtilities.CreateInstance<FaultInjectionTransactionalState<TState>>(currentContext.ActivationServices, transactionalState);
            deactivationTransactionalState.Participate(currentContext.ObservableLifecycle);
            return deactivationTransactionalState;
        }
    }

    /// <summary>
    /// Maps <see cref="FaultInjectionTransactionalStateAttribute"/> instances to transactional state facet factories.
    /// </summary>
    public class FaultInjectionTransactionalStateAttributeMapper : IAttributeToFactoryMapper<FaultInjectionTransactionalStateAttribute>
    {
        private static readonly MethodInfo create =
            typeof(IFaultInjectionTransactionalStateFactory).GetMethod("Create")!;
        /// <inheritdoc />
        public Factory<IGrainContext, object> GetFactory(ParameterInfo parameter, FaultInjectionTransactionalStateAttribute attribute)
        {
            IFaultInjectionTransactionalStateConfiguration config = attribute;
            // use generic type args to define collection type.
            MethodInfo genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            object[] args = new object[] { config };
            return context => Create(context, genericCreate, args);
        }

        private static object Create(IGrainContext context, MethodInfo genericCreate, object[] args)
        {
            IFaultInjectionTransactionalStateFactory factory = context.ActivationServices.GetRequiredService<IFaultInjectionTransactionalStateFactory>();
            return genericCreate.Invoke(factory, args)!;
        }
    }
}
