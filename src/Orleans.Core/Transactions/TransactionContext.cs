﻿
using System;
using System.Collections.Generic;
using System.Linq;
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

        public static string ToShortString(this ITransactionalResource resource)
        {
            // Meant to help humans when debugging or reading traces
            return resource.GetHashCode().ToString("x4").Substring(0,4);
        }
    }

}
