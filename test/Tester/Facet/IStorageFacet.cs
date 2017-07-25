using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace Tester
{
#region Storage feature
    public interface IStorageFacetConfig
    {
        string StorageProviderName { get; }
        string StateName { get; }
    }

    public interface IStorageFacet<TState>
    {
        string Name { get; }

        TState State { get; set; }

        Task SaveAsync();

        string GetExtendedInfo();
    }
    #endregion

    #region Storage feature wiring to facet

    [AttributeUsage(AttributeTargets.Property)]
    public class StorageFacetAttribute : GrainFacetAttribute, IStorageFacetConfig
    {
        public string StateName { get; private set; }
        public string StorageProviderName { get; }

        public StorageFacetAttribute(string storageProviderName = null, string stateName = null)
        {
            this.StorageProviderName = storageProviderName;
            this.StateName = stateName;
        }

        public override Factory<IGrainActivationContext, object> GetFactory(Type parameterType, string parameterName)
        {
            if (string.IsNullOrEmpty(this.StateName))
            {
                this.StateName = parameterName;
            }
            Type factoryType = typeof(IStorageFacetFactory<>).MakeGenericType(parameterType.GetGenericArguments());
            return context =>
            {
                // Get facet factory
                IStorageFacetFactory factory = context.ActivationServices.GetService(factoryType) as IStorageFacetFactory;

                // Create facet
                object facet = factory?.Create(this);

                // register with grain lifecycle
                facet.ParticipateInGrainLifecycle(context.ObservableLifeCycle);

                return facet;
             };
        }
    }

    public interface IStorageFacetFactory
    {
        object Create(IStorageFacetConfig config);
    }

    public interface IStorageFacetFactory<TState> : IStorageFacetFactory
    {
    }
    #endregion

    #region Storage facet extensibility via DI

    public class StorageFacetFactory<TState> : IStorageFacetFactory<TState>
    {
        public object Create(IStorageFacetConfig config)
        {
            if (config.StorageProviderName.StartsWith("Blob"))
            {
                return new BlobStorageFacet<TState>(config);
            }
            if (config.StorageProviderName.StartsWith("Table"))
            {
                return new TableStorageFacet<TState>(config);
            }

            throw new InvalidOperationException($"Provider with name {config.StorageProviderName} not found.");
        }
    }
    #endregion
}
