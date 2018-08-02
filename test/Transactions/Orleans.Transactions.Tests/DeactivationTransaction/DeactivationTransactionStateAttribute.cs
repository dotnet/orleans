using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Tests.DeactivatingInjection;

namespace Orleans.Transactions.Tests.DeactivationTransaction
{
    public interface IDeactivationTransactionalStateConfiguration : ITransactionalStateConfiguration
    {
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class DeactivationTransactionalStateAttribute : Attribute, IFacetMetadata, IDeactivationTransactionalStateConfiguration
    {
        public string StateName { get; }
        public string StorageName { get; }

        public DeactivationTransactionalStateAttribute(string stateName, string storageName = null)
        {
            this.StateName = stateName;
            this.StorageName = storageName;
        }
    }

    public interface IDeactivationTransactionalStateFactory
    {
        IDeactivationTransactionalState<TState> Create<TState>(IDeactivationTransactionalStateConfiguration config) where TState : class, new();
    }

    public class DeactivationalTransactionalStateFactory : IDeactivationTransactionalStateFactory
    {
        private IGrainActivationContext context;
        private JsonSerializerSettings serializerSettings;
        public DeactivationalTransactionalStateFactory(IGrainActivationContext context, ITypeResolver typeResolver, IGrainFactory grainFactory)
        {
            this.context = context;
            this.serializerSettings =
                TransactionalStateFactory.GetJsonSerializerSettings(typeResolver, grainFactory);
        }

        public IDeactivationTransactionalState<TState> Create<TState>(IDeactivationTransactionalStateConfiguration config) where TState : class, new()
        {
            TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(this.context.ActivationServices, config as ITransactionalStateConfiguration, this.serializerSettings, this.context);
            DeactivationTransactionalState<TState> deactivationTransactionalState = ActivatorUtilities.CreateInstance<DeactivationTransactionalState<TState>>(this.context.ActivationServices, transactionalState, this.context);
            deactivationTransactionalState.Participate(context.ObservableLifecycle);
            return deactivationTransactionalState;
        }
    }

    public class DeactivationTransactionalStateAttributeMapper : IAttributeToFactoryMapper<DeactivationTransactionalStateAttribute>
    {
        private static readonly MethodInfo create =
            typeof(IDeactivationTransactionalStateFactory).GetMethod("Create");
        public Factory<IGrainActivationContext, object> GetFactory(ParameterInfo parameter, DeactivationTransactionalStateAttribute attribute)
        {
            IDeactivationTransactionalStateConfiguration config = attribute;
            // use generic type args to define collection type.
            MethodInfo genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            object[] args = new object[] { config };
            return context => Create(context, genericCreate, args);
        }

        private object Create(IGrainActivationContext context, MethodInfo genericCreate, object[] args)
        {
            IDeactivationTransactionalStateFactory factory = context.ActivationServices.GetRequiredService<IDeactivationTransactionalStateFactory>();
            return genericCreate.Invoke(factory, args);
        }
    }
}
