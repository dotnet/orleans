using System;
using System.Collections.Generic;
using System.Text;

namespace OrleansSQLUtils.Configuration
{
    /// <summary>
    /// Options for SqlMembership
    /// </summary>
    public class SqlMembershipOptions
    {
        /// <summary>
        /// Connection string for Sql Storage
        /// </summary>
        public string ConnectionString { get; set; }
        /// <summary>
        /// The invariant name of the connector for membership's database.
        /// </summary>
        public string AdoInvariant { get; set; }
    }
}
