using System;
using System.Collections.Generic;

namespace Orleans.Storage
{
    public interface ILocalDataStore
    {
        string Etag { get; }
        string WriteRow(IList<Tuple<string, string>> keys, IDictionary<string, object> data, string eTag);
        IDictionary<string, object> ReadRow(IList<Tuple<string, string>> keys);
        IList<IDictionary<string, object>> ReadMultiRow(IList<Tuple<string, string>> keys);
        bool DeleteRow(IList<Tuple<string, string>> keys, string eTag);
        void Clear();
    }

    internal static class LocalDataStoreInstance
    {
        public static ILocalDataStore LocalDataStore { get; internal set; }
    }
}
