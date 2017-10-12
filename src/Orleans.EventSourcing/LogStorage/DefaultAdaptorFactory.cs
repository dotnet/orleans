using Orleans.LogConsistency;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.LogStorage
{
    internal class DefaultAdaptorFactory : ILogViewAdaptorFactory
    {
        public bool UsesStorageProvider
        {
            get
            {
                return true;
            }
        }

         public ILogViewAdaptor<T, E> MakeLogViewAdaptor<T, E>(ILogViewAdaptorHost<T, E> hostgrain, T initialstate, string graintypename, IStorageProvider storageProvider, ILogConsistencyProtocolServices services)
            where T : class, new() where E : class
        {
            return new LogViewAdaptor<T, E>(hostgrain, initialstate, storageProvider, graintypename, services);
        }

    }
}
