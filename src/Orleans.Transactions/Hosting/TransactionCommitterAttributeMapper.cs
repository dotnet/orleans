using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using System.Reflection;

namespace Orleans.Transactions
{
    internal class TransactionCommitterAttributeMapper : IAttributeToFactoryMapper<TransactionCommitterAttribute>
    {
        private static readonly MethodInfo create = typeof(ITransactionCommitterFactory).GetMethod("Create");

        public Factory<IGrainContext, object> GetFactory(ParameterInfo parameter, TransactionCommitterAttribute attribute)
        {
            TransactionCommitterAttribute config = attribute;
            // use generic type args to define collection type.
            MethodInfo genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            object[] args = new object[] { config };
            return context => Create(context, genericCreate, args);
        }

        private object Create(IGrainContext context, MethodInfo genericCreate, object[] args)
        {
            ITransactionCommitterFactory factory = context.ActivationServices.GetRequiredService<ITransactionCommitterFactory>();
            return genericCreate.Invoke(factory, args);
        }
    }
}
