using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.Tests
{
    public interface INoAttributionGrain : IGrainWithGuidKey
    {
        Task<Dictionary<int,List<string>>> GetNestedTransactionIds(int tier, Dictionary<int,List<ITransactionAttributionGrain>> tiers);
    }

    public interface INotSupportedAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.NotSupported)]
        Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers);
    }

    public interface IRequiredAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Required)]
        Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers);
    }

    public interface IRequiresNewAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.RequiresNew)]
        Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers);
    }

    #region wrappers
    public interface ITransactionAttributionGrain
    {
        Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers);
    }

    public static class TransactionAttributionGrainExtensions
    {
        public static ITransactionAttributionGrain GetTransactionAttributionGrain(this IGrainFactory grainFactory, Guid id, TransactionOption? option = null)
        {
            if(!option.HasValue)
            {
                return new NoAttributionGrain(grainFactory.GetGrain<INoAttributionGrain>(id));
            }
            switch(option.Value)
            {
                case TransactionOption.NotSupported:
                    return new NotSupportedAttributionGrain(grainFactory.GetGrain<INotSupportedAttributionGrain>(id));
                case TransactionOption.Required:
                    return new RequiredAttributionGrain(grainFactory.GetGrain<IRequiredAttributionGrain>(id));
                case TransactionOption.RequiresNew:
                    return new RequiresNewAttributionGrain(grainFactory.GetGrain<IRequiresNewAttributionGrain>(id));
                default:
                    throw new NotSupportedException($"Transaction option {option.Value} is not supported.");
            }
        }

        private class NoAttributionGrain : ITransactionAttributionGrain
        {
            private INoAttributionGrain grain;

            public NoAttributionGrain(INoAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        private class NotSupportedAttributionGrain : ITransactionAttributionGrain
        {
            private INotSupportedAttributionGrain grain;

            public NotSupportedAttributionGrain(INotSupportedAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        private class RequiredAttributionGrain : ITransactionAttributionGrain
        {
            private IRequiredAttributionGrain grain;

            public RequiredAttributionGrain(IRequiredAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        private class RequiresNewAttributionGrain : ITransactionAttributionGrain
        {
            private IRequiresNewAttributionGrain grain;

            public RequiresNewAttributionGrain(IRequiresNewAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }
    }
    #endregion wrappers
}
