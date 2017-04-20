using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Orleans.Storage
{
    internal class HierarchicalKeyStore :
#if !NETSTANDARD_TODO
        MarshalByRefObject,
#endif
        ILocalDataStore
    {
        public string Etag { get; private set; }

        private const string KEY_VALUE_PAIR_SEPERATOR = "+";
        private const string KEY_VALUE_SEPERATOR = "=";

        private long lastETagCounter = 1;
        [NonSerialized]
        private readonly IDictionary<string, IDictionary<string, object>> dataTable;
        private readonly int numKeyLayers;
        private readonly object lockable = new object();

        public HierarchicalKeyStore(int keyLayers)
        {
            numKeyLayers = keyLayers;
            dataTable = new Dictionary<string, IDictionary<string, object>>();
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
#if DEBUG
                var storeContents = DumpData(false);
                Trace.TraceInformation("WriteRow: Keys={0} Data={1} Store contents after = {2} New Etag = {3}",
                        StorageProviderUtils.PrintKeys(keys),
                        StorageProviderUtils.PrintData(data),
                        storeContents, Etag);
#endif
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
                IDictionary<string, object> data = GetDataStore(keys);
                
#if DEBUG
                Trace.TraceInformation("ReadMultiRow: Keys={0} returning Data={1}",
                    StorageProviderUtils.PrintKeys(keys), StorageProviderUtils.PrintData(data));
#endif
                return data;
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
                IList<IDictionary<string, object>> results = FindDataStores(keys);
#if DEBUG
                Trace.TraceInformation("ReadMultiRow: Keys={0} returning Results={1}",
                    StorageProviderUtils.PrintKeys(keys), StorageProviderUtils.PrintResults(results));
#endif
                return results;
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

            bool removedEntry = false;
            lock (lockable)
            {
                IDictionary<string, object> data;
                if (dataTable.TryGetValue(keyStr, out data))
                {
                    var kv = new KeyValuePair<string, IDictionary<string, object>>(keyStr, data);
                    dataTable.Remove(kv);
                    removedEntry = true;
                }

                // No change to Etag
#if DEBUG
                Trace.TraceInformation("DeleteRow: Keys={0} Removed={1} Data={2} Etag={3}",
                    StorageProviderUtils.PrintKeys(keys),
                    StorageProviderUtils.PrintData(data), 
                    removedEntry, Etag);
#endif
                return removedEntry;
            }
        }

        public void Clear()
        {
#if DEBUG
            Trace.TraceInformation("Clear Table");
#endif
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
                string[] keys = dataTable.Keys.ToArray();
                foreach (var key in keys)
                {
                    var data = dataTable[key];
                    sb.AppendFormat("{0} => {1}", key, StorageProviderUtils.PrintData(data)).AppendLine();
                }
            }
#if !DEBUG
            if (printDump)
#endif
            {
                Trace.TraceInformation("Dump {0} Etag={1} Data= {2}", GetType(), Etag, sb);
            }
            return sb.ToString();
        }

        private IDictionary<string, object> GetDataStore(IList<Tuple<string, string>> keys)
        {
            string keyStr = MakeStoreKey(keys);

            lock (lockable)
            {
                IDictionary<string, object> data;
                if (dataTable.ContainsKey(keyStr))
                {
                    data = dataTable[keyStr];
                }
                else
                {
                    data = new Dictionary<string, object>(); // Empty data set
                    dataTable[keyStr] = data;
                }
#if DEBUG
                Trace.TraceInformation("Read: {0}", StorageProviderUtils.PrintOneWrite(keys, data, null));
#endif
                return data;
            }
        }

        private IList<IDictionary<string, object>> FindDataStores(IList<Tuple<string, string>> keys)
        {
            var results = new List<IDictionary<string, object>>();

            if (numKeyLayers == keys.Count)
            {
                results.Add(GetDataStore(keys));
            }
            else
            {
                string keyStr = MakeStoreKey(keys);

                lock (lockable)
                {
                    foreach (var key in dataTable.Keys)
                    if (key.StartsWith(keyStr))
                        results.Add(dataTable[key]);
                }
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

                sb.Append(keyPair.Item1 + KEY_VALUE_SEPERATOR + keyPair.Item2);
            }
            return sb.ToString();
        }

        private string NewEtag()
        {
            return lastETagCounter++.ToString(CultureInfo.InvariantCulture);
        }
    }
}
