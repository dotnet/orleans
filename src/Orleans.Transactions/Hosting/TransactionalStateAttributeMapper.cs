using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionalStateAttributeMapper : TransactionalStateAttributeMapper<TransactionalStateAttribute>
    {
        protected override TransactionalStateConfiguration AttributeToConfig(TransactionalStateAttribute attribute) => new TransactionalStateConfiguration(attribute);
    }

    public abstract class TransactionalStateAttributeMapper<TAttribute> : IAttributeToFactoryMapper<TAttribute>
        where TAttribute : IFacetMetadata, ITransactionalStateConfiguration
    {
        private static readonly MethodInfo create = typeof(ITransactionalStateFactory).GetMethod("Create");

        public Factory<IGrainContext, object> GetFactory(ParameterInfo parameter, TAttribute attribute)
        {
            var config = AttributeToConfig(attribute);
            // use generic type args to define collection type.
            var genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            var args = new object[] { config };
            return context => Create(context, genericCreate, args);
        }

        private object Create(IGrainContext context, MethodInfo genericCreate, object[] args)
        {
            var factory = context.ActivationServices.GetRequiredService<ITransactionalStateFactory>();
            return genericCreate.Invoke(factory, args);
        }

        protected abstract TransactionalStateConfiguration AttributeToConfig(TAttribute attribute);
    }
}
