using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

#if CLUSTERING_ADONET
namespace Orleans.Clustering.AdoNet.Storage
#elif PERSISTENCE_ADONET
namespace Orleans.Persistence.AdoNet.Storage
#elif REMINDERS_ADONET
namespace Orleans.Reminders.AdoNet.Storage
#elif STATISTICS_ADONET
namespace Orleans.Statistics.AdoNet.Storage
#elif TESTER_SQLUTILS
namespace Orleans.Tests.SqlUtils
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    internal interface ICommandInterceptor
    {
        void Intercept(IDbCommand command);
    }
}
