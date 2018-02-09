using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.AdoNet.Options
{
    public class AdoNetGatewayListProviderOptions
    {
        /// <summary>
        /// Connection string for Sql
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// The invariant name of the connector for gatewayProvider's database.
        /// </summary>
        public string AdoInvariant { get; set; }
    }
}
