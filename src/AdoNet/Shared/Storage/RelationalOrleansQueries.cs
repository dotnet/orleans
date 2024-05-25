using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.AdoNet.Core;
using Orleans.Runtime;

#if CLUSTERING_ADONET
namespace Orleans.Clustering.AdoNet.Storage
#elif PERSISTENCE_ADONET
namespace Orleans.Persistence.AdoNet.Storage
#elif REMINDERS_ADONET
namespace Orleans.Reminders.AdoNet.Storage
#elif STREAMING_ADONET

namespace Orleans.Streaming.AdoNet.Storage
#elif TESTER_SQLUTILS
using Orleans.Streaming.AdoNet;
namespace Orleans.Tests.SqlUtils
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// A class for all relational storages that support all systems stores : membership, reminders and statistics
    /// </summary>
    internal class RelationalOrleansQueries
    {

#if REMINDERS_ADONET || TESTER_SQLUTILS


#endif

#if CLUSTERING_ADONET || TESTER_SQLUTILS


#endif

#if STREAMING_ADONET || TESTER_SQLUTILS


#endif
    }
}