
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Transactions
{
    public static class TransactionContext
    {
        internal const string TransactionInfoHeader = "#TC_TI";
        internal const string Orleans_TransactionContext_Key = "#ORL_TC";

        public static ITransactionInfo GetTransactionInfo()
        {
            Dictionary<string, object> values = GetContextData();
            object result;
            if ((values != null) && values.TryGetValue(TransactionInfoHeader, out result))
            {
                return result as ITransactionInfo;
            }
            return null;
        }


        public static T GetRequiredTransactionInfo<T>() where T : class, ITransactionInfo
        {
            var result = GetTransactionInfo();
            if (result == null)
            {
                throw new OrleansTransactionException($"A transaction context is required for access. Did you forget a [TransactionOption.Required] annotation?");
            }
            else if (result is T info)
            {
                return info;
            }
            else  
            {
                throw new OrleansTransactionException($"Configuration error: transaction agent is using a different protocol ({result.GetType().FullName}) than the participant expects ({typeof(T).FullName}).");
            }
        }

        internal static void SetTransactionInfo(ITransactionInfo info)
        {
            Dictionary<string, object> values = GetContextData();

            values = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values);
            values[TransactionInfoHeader] = info;
            SetContextData(values);
        }

        internal static void Clear()
        {
            // Remove the key to prevent passing of its value from this point on
            RequestContext.Remove(Orleans_TransactionContext_Key);
        }

        private static void SetContextData(Dictionary<string, object> values)
        {
            RequestContext.Set(Orleans_TransactionContext_Key, values);
        }

        private static Dictionary<string, object> GetContextData()
        {
            return (Dictionary<string, object>)RequestContext.Get(Orleans_TransactionContext_Key);
        }
    }
}
