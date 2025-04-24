using System.Collections.Generic;
using Orleans.Transactions.AdoNet.Storage;

namespace Orleans.Transactions.AdoNet.TransactionalState
{
    public class TransactionalStateStorageOptions
    {
        /// <summary>
        /// The default ADO.NET invariant used for storage if none is given. 
        /// </summary>
        public const string DEFAULT_ADONET_INVARIANT = AdoNetInvariants.InvariantNameSqlServer;

        /// <summary>
        /// The invariant name for storage.
        /// </summary>
        public string Invariant { get; set; } = DEFAULT_ADONET_INVARIANT;

        /// <summary>
        /// connection string
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// the table name of StateEntity in database
        /// </summary>
        public string StateEntityTableName { get; set; } = "orleanstransactionstatetable";

        /// <summary>
        /// the table name of KeyEntity in database
        /// </summary>
        public string KeyEntityTableName { get; set; } = "orleanstransactionkeytable";

        /// <summary>
        ///  the database parameter dot
        /// </summary>
        public string SqlParameterDot { get; set; } = "@";

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        internal Dictionary<string, string> ExecuteSqlDcitionary { get; set; } = new Dictionary<string, string>();
    }
}
