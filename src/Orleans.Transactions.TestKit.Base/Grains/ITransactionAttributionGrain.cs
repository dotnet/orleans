using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Defines a grain call with no explicit transaction attribution.
    /// </summary>
    public interface INoAttributionGrain : IGrainWithGuidKey
    {
        /// <inheritdoc cref="ITransactionAttributionGrain.GetNestedTransactionIds(int, List{ITransactionAttributionGrain}[])"/>
        Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    /// <summary>
    /// Defines a grain call which suppresses the ambient transaction.
    /// </summary>
    public interface ISuppressAttributionGrain : IGrainWithGuidKey
    {
        /// <inheritdoc cref="ITransactionAttributionGrain.GetNestedTransactionIds(int, List{ITransactionAttributionGrain}[])"/>
        [Transaction(TransactionOption.Suppress)]
        Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    /// <summary>
    /// Defines a grain call which joins the ambient transaction or creates one when none exists.
    /// </summary>
    public interface ICreateOrJoinAttributionGrain : IGrainWithGuidKey
    {
        /// <inheritdoc cref="ITransactionAttributionGrain.GetNestedTransactionIds(int, List{ITransactionAttributionGrain}[])"/>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    /// <summary>
    /// Defines a grain call which creates a new transaction.
    /// </summary>
    public interface ICreateAttributionGrain : IGrainWithGuidKey
    {
        /// <inheritdoc cref="ITransactionAttributionGrain.GetNestedTransactionIds(int, List{ITransactionAttributionGrain}[])"/>
        [Transaction(TransactionOption.Create)]
        Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    /// <summary>
    /// Defines a grain call which requires and joins an ambient transaction.
    /// </summary>
    public interface IJoinAttributionGrain : IGrainWithGuidKey
    {
        /// <inheritdoc cref="ITransactionAttributionGrain.GetNestedTransactionIds(int, List{ITransactionAttributionGrain}[])"/>
        [Transaction(TransactionOptionAlias.Mandatory)]
        Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    /// <summary>
    /// Defines a grain call which joins an ambient transaction when one exists and otherwise runs without one.
    /// </summary>
    public interface ISupportedAttributionGrain : IGrainWithGuidKey
    {
        /// <inheritdoc cref="ITransactionAttributionGrain.GetNestedTransactionIds(int, List{ITransactionAttributionGrain}[])"/>
        [Transaction(TransactionOption.Supported)]
        Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    /// <summary>
    /// Defines a grain call which runs without a transaction and rejects calls made within one.
    /// </summary>
    public interface INotAllowedAttributionGrain : IGrainWithGuidKey
    {
        /// <inheritdoc cref="ITransactionAttributionGrain.GetNestedTransactionIds(int, List{ITransactionAttributionGrain}[])"/>
        [Transaction(TransactionOption.NotAllowed)]
        Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    #region wrappers

    /// <summary>
    /// Provides a common interface over grain references with different transaction attribution options.
    /// </summary>
    public interface ITransactionAttributionGrain
    {
        /// <summary>
        /// Records the transaction identifier observed at the current tier and recursively invokes the remaining tiers.
        /// </summary>
        /// <param name="tier">The zero-based tier currently being executed.</param>
        /// <param name="tiers">The remaining tiers, where each element contains the grains invoked at that tier.</param>
        /// <returns>
        /// An array indexed by tier whose populated entries contain the transaction identifiers observed by grains at that tier.
        /// A <see langword="null"/> identifier indicates that the call ran without a transaction.
        /// </returns>
        Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers);
    }

    /// <summary>
    /// Provides helpers and serializable adapters for transaction attribution grain references.
    /// </summary>
    public static class TransactionAttributionGrainExtensions
    {
        /// <summary>
        /// Gets a transaction attribution grain adapter for the specified transaction option.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to resolve the grain reference.</param>
        /// <param name="id">The grain primary key.</param>
        /// <param name="option">
        /// The transaction option to apply, or <see langword="null"/> to use a grain interface without explicit attribution.
        /// </param>
        /// <returns>An adapter over the grain interface corresponding to <paramref name="option"/>.</returns>
        /// <exception cref="NotSupportedException"><paramref name="option"/> is not a supported transaction option.</exception>
        public static ITransactionAttributionGrain GetTransactionAttributionGrain(this IGrainFactory grainFactory, Guid id, TransactionOption? option = null)
        {
            if (!option.HasValue)
            {
                return new NoAttributionGrain(grainFactory.GetGrain<INoAttributionGrain>(id));
            }
            switch (option.Value)
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

        /// <summary>
        /// Adapts a grain reference with no explicit transaction attribution.
        /// </summary>
        [GenerateSerializer]
        public class NoAttributionGrain : ITransactionAttributionGrain
        {
            /// <summary>
            /// The underlying grain reference.
            /// </summary>
            [Id(0)]
            public INoAttributionGrain grain;

            /// <summary>
            /// Initializes a new instance of the <see cref="NoAttributionGrain"/> class.
            /// </summary>
            /// <param name="grain">The underlying grain reference.</param>
            public NoAttributionGrain(INoAttributionGrain grain)
            {
                this.grain = grain;
            }

            /// <inheritdoc/>
            public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        /// <summary>
        /// Adapts a grain reference which suppresses the ambient transaction.
        /// </summary>
        [GenerateSerializer]
        public class SuppressAttributionGrain : ITransactionAttributionGrain
        {
            /// <summary>
            /// The underlying grain reference.
            /// </summary>
            [Id(0)]
            public ISuppressAttributionGrain grain;

            /// <summary>
            /// Initializes a new instance of the <see cref="SuppressAttributionGrain"/> class.
            /// </summary>
            /// <param name="grain">The underlying grain reference.</param>
            public SuppressAttributionGrain(ISuppressAttributionGrain grain)
            {
                this.grain = grain;
            }

            /// <inheritdoc/>
            public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        /// <summary>
        /// Adapts a grain reference which joins the ambient transaction or creates one when none exists.
        /// </summary>
        [GenerateSerializer]
        public class CreateOrJoinAttributionGrain : ITransactionAttributionGrain
        {
            /// <summary>
            /// The underlying grain reference.
            /// </summary>
            [Id(0)]
            public ICreateOrJoinAttributionGrain grain;

            /// <summary>
            /// Initializes a new instance of the <see cref="CreateOrJoinAttributionGrain"/> class.
            /// </summary>
            /// <param name="grain">The underlying grain reference.</param>
            public CreateOrJoinAttributionGrain(ICreateOrJoinAttributionGrain grain)
            {
                this.grain = grain;
            }

            /// <inheritdoc/>
            public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        /// <summary>
        /// Adapts a grain reference which creates a new transaction.
        /// </summary>
        [GenerateSerializer]
        public class CreateAttributionGrain : ITransactionAttributionGrain
        {
            /// <summary>
            /// The underlying grain reference.
            /// </summary>
            [Id(0)]
            public ICreateAttributionGrain grain;

            /// <summary>
            /// Initializes a new instance of the <see cref="CreateAttributionGrain"/> class.
            /// </summary>
            /// <param name="grain">The underlying grain reference.</param>
            public CreateAttributionGrain(ICreateAttributionGrain grain)
            {
                this.grain = grain;
            }

            /// <inheritdoc/>
            public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        /// <summary>
        /// Adapts a grain reference which requires and joins an ambient transaction.
        /// </summary>
        [GenerateSerializer]
        public class JoinAttributionGrain : ITransactionAttributionGrain
        {
            /// <summary>
            /// The underlying grain reference.
            /// </summary>
            [Id(0)]
            public IJoinAttributionGrain grain;

            /// <summary>
            /// Initializes a new instance of the <see cref="JoinAttributionGrain"/> class.
            /// </summary>
            /// <param name="grain">The underlying grain reference.</param>
            public JoinAttributionGrain(IJoinAttributionGrain grain)
            {
                this.grain = grain;
            }

            /// <inheritdoc/>
            public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        /// <summary>
        /// Adapts a grain reference which supports an ambient transaction when one exists.
        /// </summary>
        [GenerateSerializer]
        public class SupportedAttributionGrain : ITransactionAttributionGrain
        {
            /// <summary>
            /// The underlying grain reference.
            /// </summary>
            [Id(0)]
            public ISupportedAttributionGrain grain;

            /// <summary>
            /// Initializes a new instance of the <see cref="SupportedAttributionGrain"/> class.
            /// </summary>
            /// <param name="grain">The underlying grain reference.</param>
            public SupportedAttributionGrain(ISupportedAttributionGrain grain)
            {
                this.grain = grain;
            }

            /// <inheritdoc/>
            public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }

        /// <summary>
        /// Adapts a grain reference which rejects calls made within a transaction.
        /// </summary>
        [GenerateSerializer]
        public class NotAllowedAttributionGrain : ITransactionAttributionGrain
        {
            /// <summary>
            /// The underlying grain reference.
            /// </summary>
            [Id(0)]
            public INotAllowedAttributionGrain grain;

            /// <summary>
            /// Initializes a new instance of the <see cref="NotAllowedAttributionGrain"/> class.
            /// </summary>
            /// <param name="grain">The underlying grain reference.</param>
            public NotAllowedAttributionGrain(INotAllowedAttributionGrain grain)
            {
                this.grain = grain;
            }

            /// <inheritdoc/>
            public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
            {
                return this.grain.GetNestedTransactionIds(tier, tiers);
            }
        }
    }
    #endregion wrappers
}
