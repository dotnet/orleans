using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Orleans.Storage
{
    internal class HierarchicalKeyStore : ILocalDataStore
    {
        public string Etag { get; private set; }

        private const char KEY_VALUE_PAIR_SEPERATOR = '+';
        private const char KEY_VALUE_SEPERATOR = '=';

        private long lastETagCounter = 1;
        [NonSerialized]
        private readonly Dictionary<string, Dictionary<string, object>> dataTable = new();
        private readonly int numKeyLayers;
        private readonly object lockable = new object();

        public HierarchicalKeyStore(int keyLayers)
        {
            numKeyLayers = keyLayers;
        }

        public string WriteRow(IList<Tuple<string, string>> keys, IDictionary<string, object> data, string eTag)
        {
            if (keys.Count > numKeyLayers)
            {
                var error = string.Format("Wrong number of keys supplied -- Expected count = {0} Received = {1}", numKeyLayers, keys.Count);
                Trace.TraceError(error);
                throw new ArgumentOutOfRangeException("keys", keys.Count, error);
            }

            lock (lockable)
            {
                var storedData = GetDataStore(keys);

                foreach (var kv in data)
                    storedData[kv.Key] = kv.Value;

                Etag = NewEtag();
                return Etag;
            }
        }

        public IDictionary<string, object> ReadRow(IList<Tuple<string, string>> keys)
        {
            if (keys.Count > numKeyLayers)
            {
                var error = string.Format("Not enough keys supplied -- Expected count = {0} Received = {1}", numKeyLayers, keys.Count);
                Trace.TraceError(error);
                throw new ArgumentOutOfRangeException("keys", keys.Count, error);
            }

            lock (lockable)
            {
                return GetDataStore(keys);
            }
        }

        public IList<IDictionary<string, object>> ReadMultiRow(IList<Tuple<string, string>> keys)
        {
            if (keys.Count > numKeyLayers)
            {
                throw new ArgumentOutOfRangeException("keys", keys.Count,
                    string.Format("Too many key supplied -- Expected count = {0} Received = {1}", numKeyLayers, keys.Count));
            }

            lock (lockable)
            {
                return FindDataStores(keys);
            }
        }

        public bool DeleteRow(IList<Tuple<string, string>> keys, string eTag)
        {
            if (keys.Count > numKeyLayers)
            {
                throw new ArgumentOutOfRangeException("keys", keys.Count,
                    string.Format("Not enough keys supplied -- Expected count = {0} Received = {1}", numKeyLayers, keys.Count));
            }

            string keyStr = MakeStoreKey(keys);

            lock (lockable)
            {
                // No change to Etag
                return dataTable.Remove(keyStr);
            }
        }

        public void Clear()
        {
            lock (lockable)
            {
                dataTable.Clear();
            }
        }

        public string DumpData(bool printDump = true)
        {
            var sb = new StringBuilder();
            lock (lockable)
            {
                foreach (var kv in dataTable)
                {
                    sb.AppendFormat("{0} => {1}", kv.Key, kv.Value).AppendLine();
                }
            }
            if (printDump)
            {
                Trace.TraceInformation("Dump {0} Etag={1} Data= {2}", GetType(), Etag, sb);
            }
            return sb.ToString();
        }

        private Dictionary<string, object> GetDataStore(IList<Tuple<string, string>> keys)
        {
            string keyStr = MakeStoreKey(keys);

            lock (lockable)
            {
                if (!dataTable.TryGetValue(keyStr, out var data))
                {
                    data = new Dictionary<string, object>(); // Empty data set
                    dataTable[keyStr] = data;
                }
                return data;
            }
        }

        private IList<IDictionary<string, object>> FindDataStores(IList<Tuple<string, string>> keys)
        {
            if (numKeyLayers == keys.Count)
            {
                return new[] { GetDataStore(keys) };
            }

            var results = new List<IDictionary<string, object>>();
            string keyStr = MakeStoreKey(keys);

            lock (lockable)
            {
                foreach (var kv in dataTable)
                    if (kv.Key.StartsWith(keyStr, StringComparison.Ordinal))
                        results.Add(kv.Value);
            }
            return results;
        }

        internal static string MakeStoreKey(IEnumerable<Tuple<string, string>> keys)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var keyPair in keys)
            {
                if (first)
                    first = false;
                else
                    sb.Append(KEY_VALUE_PAIR_SEPERATOR);

                sb.Append(keyPair.Item1).Append(KEY_VALUE_SEPERATOR).Append(keyPair.Item2);
            }
            return sb.ToString();
        }

        private string NewEtag()
        {
            return lastETagCounter++.ToString(CultureInfo.InvariantCulture);
        }
    }
}
