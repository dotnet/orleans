using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    public class NoAttributionGrain : Grain, INoAttributionGrain
    {
        public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class SuppressAttributionGrain : Grain, ISuppressAttributionGrain
    {
        public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class CreateOrJoinAttributionGrain : Grain, ICreateOrJoinAttributionGrain
    {
        public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class CreateAttributionGrain : Grain, ICreateAttributionGrain
    {
        public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class JoinAttributionGrain : Grain, IJoinAttributionGrain
    {
        public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class SupportedAttributionGrain : Grain, ISupportedAttributionGrain
    {
        public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class NotAllowedAttributionGrain : Grain, INotAllowedAttributionGrain
    {
        public Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    internal static class AttributionGrain
    {
        public static async Task<List<string>[]> GetNestedTransactionIds(int tier, List<ITransactionAttributionGrain>[] tiers)
        {
            TransactionInfo ti = TransactionContext.GetTransactionInfo();
            List<string>[] results = new List<string>[tier + 1 + tiers.Length];
            results[tier] = new List<string>(new[] { ti?.Id });

            if (tiers.Length == 0)
            {
                return results;
            }

            List<ITransactionAttributionGrain> nextTier = tiers.FirstOrDefault();
            List<ITransactionAttributionGrain>[] nextTiers = tiers.Skip(1).ToArray();
            List<string>[][] tiersResults = await Task.WhenAll(nextTier.Select(g => g.GetNestedTransactionIds(tier+1, nextTiers)));
            foreach (List<string>[] result in tiersResults)
            {
                if (result.Length != results.Length) throw new ApplicationException("Invalid result length");
                for (int i = tier + 1; i < results.Length; i++)
                {
                    if (results[i] != null)
                    {
                        results[i].AddRange(result[i]);
                    }
                    else
                        results[i] = result[i];
                }
            }

            return results;
        }
    }
}
