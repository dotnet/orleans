using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Records transaction attribution for a grain call with no explicit transaction option.
    /// </summary>
    public class NoAttributionGrain : Grain, INoAttributionGrain
    {
        /// <inheritdoc/>
        public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    /// <summary>
    /// Records transaction attribution for a grain call which suppresses the ambient transaction.
    /// </summary>
    public class SuppressAttributionGrain : Grain, ISuppressAttributionGrain
    {
        /// <inheritdoc/>
        public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    /// <summary>
    /// Records transaction attribution for a grain call which joins or creates a transaction.
    /// </summary>
    public class CreateOrJoinAttributionGrain : Grain, ICreateOrJoinAttributionGrain
    {
        /// <inheritdoc/>
        public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    /// <summary>
    /// Records transaction attribution for a grain call which creates a new transaction.
    /// </summary>
    public class CreateAttributionGrain : Grain, ICreateAttributionGrain
    {
        /// <inheritdoc/>
        public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    /// <summary>
    /// Records transaction attribution for a grain call which requires and joins an ambient transaction.
    /// </summary>
    public class JoinAttributionGrain : Grain, IJoinAttributionGrain
    {
        /// <inheritdoc/>
        public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    /// <summary>
    /// Records transaction attribution for a grain call which supports an ambient transaction.
    /// </summary>
    public class SupportedAttributionGrain : Grain, ISupportedAttributionGrain
    {
        /// <inheritdoc/>
        public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    /// <summary>
    /// Records transaction attribution for a grain call which rejects an ambient transaction.
    /// </summary>
    public class NotAllowedAttributionGrain : Grain, INotAllowedAttributionGrain
    {
        /// <inheritdoc/>
        public Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    internal static class AttributionGrain
    {
        public static async Task<List<string?>?[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            TransactionInfo? ti = TransactionContext.GetTransactionInfo();
            List<string?>?[] results = new List<string?>[tier + 1 + tiers.Length];
            results[tier] = new List<string?>(new[] { ti?.Id });

            if (tiers.Length == 0)
            {
                return results;
            }

            List<ITransactionAttributionGrain> nextTier = tiers.FirstOrDefault()!;
            List<ITransactionAttributionGrain>[] nextTiers = tiers.Skip(1).ToArray();
            List<string?>?[][] tiersResults = await Task.WhenAll(nextTier.Select(g => g.GetNestedTransactionIds(tier+1, nextTiers)));
            foreach (List<string?>?[] result in tiersResults)
            {
                if (result.Length != results.Length) throw new ApplicationException("Invalid result length");
                for (int i = tier + 1; i < results.Length; i++)
                {
                    if (results[i] != null)
                    {
                        results[i]!.AddRange(result[i]!);
                    }
                    else
                        results[i] = result[i];
                }
            }

            return results;
        }
    }
}
