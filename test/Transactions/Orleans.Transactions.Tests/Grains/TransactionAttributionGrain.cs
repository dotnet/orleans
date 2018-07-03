using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Transactions.Tests.Grains
{
    public class NoAttributionGrain : Grain, INoAttributionGrain
    {
        public Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class NotSupportedAttributionGrain : Grain, INotSupportedAttributionGrain
    {
        public Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class RequiredAttributionGrain : Grain, IRequiredAttributionGrain
    {
        public Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    public class RequiresNewAttributionGrain : Grain, IRequiresNewAttributionGrain
    {
        public Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
        {
            return AttributionGrain.GetNestedTransactionIds(tier, tiers);
        }
    }

    internal static class AttributionGrain
    {
        static public async Task<Dictionary<int, List<string>>> GetNestedTransactionIds(int tier, Dictionary<int, List<ITransactionAttributionGrain>> tiers)
        {
            ITransactionInfo ti = TransactionContext.GetTransactionInfo();
            Dictionary<int, List<string>> results = new Dictionary<int, List<string>>();
            results[tier] = new List<string>(new[] { ti?.Id });

            if (tiers.Count == 0)
            {
                return results;
            }

            KeyValuePair<int, List<ITransactionAttributionGrain>> nextTier = tiers.FirstOrDefault();
            Dictionary<int, List<ITransactionAttributionGrain>> nextTiers = tiers.OrderBy(kvp => kvp.Key).Skip(1).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            Dictionary<int, List<string>>[] tiersResults = await Task.WhenAll(nextTier.Value.Select(g => g.GetNestedTransactionIds(nextTier.Key, nextTiers)));
            foreach (Dictionary<int, List<string>> result in tiersResults)
            {
                foreach (KeyValuePair<int, List<string>> kvp in result)
                {
                    if (results.TryGetValue(kvp.Key, out List<string> ids))
                    {
                        ids.AddRange(kvp.Value);
                    }
                    else
                        results[kvp.Key] = kvp.Value;
                }
            }

            return results;
        }
    }
}
