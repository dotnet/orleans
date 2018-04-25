using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions.AzureStorage.Storage.Development
{
    /// <summary>
    /// Option class to configure Azure transaction log archive behavior
    /// </summary>
    public class AzureTransactionArchiveLogOptions
    {
        public const bool DEFAULT_ARCHIVE_LOG = false;
        //whether to archive commited transaction log or not. turned off by default
        public bool ArchiveLog { get; set; } = DEFAULT_ARCHIVE_LOG;
    }
}
