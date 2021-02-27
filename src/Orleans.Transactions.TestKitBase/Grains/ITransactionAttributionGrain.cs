using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    public interface INoAttributionGrain : IGrainWithGuidKey
    {
        Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    public interface ISuppressAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Suppress)]
        Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    public interface ICreateOrJoinAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    public interface ICreateAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Create)]
        Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    public interface IJoinAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOptionAlias.Mandatory)]
        Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    public interface ISupportedAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Supported)]
        Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    public interface INotAllowedAttributionGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.NotAllowed)]
        Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    #region wrappers
    public interface ITransactionAttributionGrain
    {
        Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
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
                case TransactionOption.Suppress:
                    return new SuppressAttributionGrain(grainFactory.GetGrain<ISuppressAttributionGrain>(id));
                case TransactionOption.CreateOrJoin:
                    return new CreateOrJoinAttributionGrain(grainFactory.GetGrain<ICreateOrJoinAttributionGrain>(id));
                case TransactionOption.Create:
                    return new CreateAttributionGrain(grainFactory.GetGrain<ICreateAttributionGrain>(id));
                case TransactionOption.Join:
                    return new JoinAttributionGrain(grainFactory.GetGrain<IJoinAttributionGrain>(id));
                case TransactionOption.Supported:
                    return new SupportedAttributionGrain(grainFactory.GetGrain<ISupportedAttributionGrain>(id));
                case TransactionOption.NotAllowed:
                    return new NotAllowedAttributionGrain(grainFactory.GetGrain<INotAllowedAttributionGrain>(id));
                default:
                    throw new NotSupportedException($"Transaction option {option.Value} is not supported.");
            }
        }

        [GenerateSerializer]
        public class NoAttributionGrain : ITransactionAttributionGrain
        {
            [Id(0)]
            public INoAttributionGrain grain;

            public NoAttributionGrain(INoAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        [GenerateSerializer]
        public class SuppressAttributionGrain : ITransactionAttributionGrain
        {
            [Id(0)]
            public ISuppressAttributionGrain grain;

            public SuppressAttributionGrain(ISuppressAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        [GenerateSerializer]
        public class CreateOrJoinAttributionGrain : ITransactionAttributionGrain
        {
            [Id(0)]
            public ICreateOrJoinAttributionGrain grain;

            public CreateOrJoinAttributionGrain(ICreateOrJoinAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        [GenerateSerializer]
        public class CreateAttributionGrain : ITransactionAttributionGrain
        {
            [Id(0)]
            public ICreateAttributionGrain grain;

            public CreateAttributionGrain(ICreateAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        [GenerateSerializer]
        public class JoinAttributionGrain : ITransactionAttributionGrain
        {
            [Id(0)]
            public IJoinAttributionGrain grain;

            public JoinAttributionGrain(IJoinAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        [GenerateSerializer]
        public class SupportedAttributionGrain : ITransactionAttributionGrain
        {
            [Id(0)]
            public ISupportedAttributionGrain grain;

            public SupportedAttributionGrain(ISupportedAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        [GenerateSerializer]
        public class NotAllowedAttributionGrain : ITransactionAttributionGrain
        {
            [Id(0)]
            public INotAllowedAttributionGrain grain;

            public NotAllowedAttributionGrain(INotAllowedAttributionGrain grain)
            {
                this.grain = grain;
            }

            public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }
    }
    #endregion wrappers
}
