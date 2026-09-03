using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Maps <see cref="TransactionalStateAttribute"/> parameters to transactional state instances.
    /// </summary>
    public class TransactionalStateAttributeMapper : TransactionalStateAttributeMapper<TransactionalStateAttribute>
    {
        /// <inheritdoc/>
        protected override TransactionalStateConfiguration AttributeToConfig(TransactionalStateAttribute attribute)
        {
            return new TransactionalStateConfiguration(attribute);
        }
    }

    /// <summary>
    /// Maps transactional state facet attributes to transactional state instances.
    /// </summary>
    /// <typeparam name="TAttribute">The transactional state facet attribute type.</typeparam>
    public abstract class TransactionalStateAttributeMapper<TAttribute> : IAttributeToFactoryMapper<TAttribute>
        where TAttribute : IFacetMetadata, ITransactionalStateConfiguration
    {
        private static readonly MethodInfo create = typeof(ITransactionalStateFactory).GetMethod("Create")!;

        /// <inheritdoc/>
        public Factory<IGrainContext, object> GetFactory(ParameterInfo parameter, TAttribute attribute)
        {
            TransactionalStateConfiguration config = AttributeToConfig(attribute);
            // use generic type args to define collection type.
            MethodInfo genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            object[] args = new object[] { config };
            return context => Create(context, genericCreate, args);
        }

        private object Create(IGrainContext context, MethodInfo genericCreate, object[] args)
        {
            ITransactionalStateFactory factory = context.ActivationServices.GetRequiredService<ITransactionalStateFactory>();
            return genericCreate.Invoke(factory, args)!;
        }

        /// <summary>
        /// Creates transactional state configuration from a facet attribute.
        /// </summary>
        /// <param name="attribute">The transactional state facet attribute.</param>
        /// <returns>The transactional state configuration.</returns>
        protected abstract TransactionalStateConfiguration AttributeToConfig(TAttribute attribute);
    }
}
