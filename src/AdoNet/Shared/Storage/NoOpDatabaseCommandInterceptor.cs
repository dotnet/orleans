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
#elif TESTER_SQLUTILS
namespace Orleans.Tests.SqlUtils
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    internal class NoOpCommandInterceptor : ICommandInterceptor
    {
        public static readonly ICommandInterceptor Instance = new NoOpCommandInterceptor();

        private NoOpCommandInterceptor()
        {
            
        }

        public void Intercept(IDbCommand command)
        {
            //NOP
        }
    }
}
