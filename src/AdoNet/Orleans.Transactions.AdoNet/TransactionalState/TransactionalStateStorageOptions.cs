using System.Collections.Generic;
using Orleans.Transactions.AdoNet.Storage;
using Orleans.Transactions.AdoNet.Utils;

namespace Orleans.Transactions.AdoNet.TransactionalState
{
    /// <summary>
    /// Configures an ADO.NET transactional state storage provider.
    /// </summary>
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
        /// The connection string used to access the database.
        /// </summary>
        public string ConnectionString { get; set; } = null!;

        /// <summary>
        /// The transactional state table name.
        /// </summary>
        public string StateEntityTableName { get; set; } = "OrleansTransactionStateTable";

        /// <summary>
        /// The transactional state key table name.
        /// </summary>
        public string KeyEntityTableName { get; set; } = "OrleansTransactionKeyTable";

        /// <summary>
        /// The database parameter prefix.
        /// </summary>
        public string SqlParameterDot { get; set; } = Constants.SqlParameterDot;

        /// <summary>
        /// The maximum supported state identifier length.
        /// </summary>
        public int StateIdKeyMaxLength { get; set; } = 255;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        internal Dictionary<string, string> ExecuteSqlDictionary { get; } = new();
    }
}
