using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using System.Reflection;

namespace Orleans.Transactions
{
    internal class TransactionalStateAttributeMapper : IAttributeToFactoryMapper<TransactionalStateAttribute>
    {
        private static readonly MethodInfo create = typeof(ITransactionalStateFactory).GetMethod("Create");

        public Factory<IGrainActivationContext, object> GetFactory(ParameterInfo parameter, TransactionalStateAttribute attribute)
        {
            ITransactionalStateConfiguration config = attribute;
            // use generic type args to define collection type.
            MethodInfo genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            object[] args = new object[] { config };
            return context => Create(context, genericCreate, args);
        }

        private object Create(IGrainActivationContext context, MethodInfo genericCreate, object[] args)
        {
            ITransactionalStateFactory factory = context.ActivationServices.GetRequiredService<ITransactionalStateFactory>();
            return genericCreate.Invoke(factory, args);
        }
    }
}
