using System;
using System.Collections.Generic;
using System.Text;

using Orleans.Runtime;

namespace Orleans.Storage
{
    public class StorageProviderUtils
    {
        public static int PositiveHash(GrainReference grainReference, int hashRange)
        {
            int hash = unchecked((int)grainReference.GetUniformHashCode());
            int positiveHash = ((hash % hashRange) + hashRange) % hashRange;
            return positiveHash;
        }
        public static int PositiveHash(int hash, int hashRange)
        {
            int positiveHash = ((hash % hashRange) + hashRange) % hashRange;
            return positiveHash;
        }

        public static string PrintKeys(IEnumerable<Tuple<string, string>> keys)
        {
            return Utils.EnumerableToString(keys,
                keyTuple => string.Format("Key:{0}={1}", keyTuple.Item1, keyTuple.Item2 ?? "null"));
        }

        public static string PrintData(object data)
        {
            if (data == null)
            {
                return "[ ]";
            }

            return data.ToString();
        }

        public static string PrintResults(IList<IDictionary<string, object>> results)
        {
            if (results == null || results.Count == 0)
                return "0 Results";
            
            var sb = new StringBuilder();
            sb.Append(results.Count).Append(" Results= ( ").AppendLine();
            foreach (var data in results)
                sb.Append("[ ").Append(PrintData(data)).Append(" ] ").AppendLine();
            
            sb.Append(")");
            return sb.ToString();
        }

        public static string PrintOneWrite(
            IEnumerable<Tuple<string, string>> keys,
            object data,
            string eTag)
        {
            var sb = new StringBuilder();
            sb.Append("Keys=").Append(PrintKeys(keys));
            sb.Append(" Data=").Append(PrintData(data));
            sb.Append(" Etag=").Append(eTag ?? "null");
            return sb.ToString();
        }
    }
}
