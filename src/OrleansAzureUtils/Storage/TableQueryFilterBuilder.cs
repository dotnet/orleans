
using Microsoft.WindowsAzure.Storage.Table;

namespace Orleans.AzureUtils
{
    /// <summary>
    /// Helper functions for building table queries.
    /// </summary>
    internal class TableQueryFilterBuilder
    {
        /// <summary>
        /// Builds query string to match partitionkey
        /// </summary>
        /// <param name="partitionKey"></param>
        /// <returns></returns>
        public static string MatchPartitionKeyFilter(string partitionKey)
        {
            return TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, partitionKey);
        }

        /// <summary>
        /// Builds query string to match rowkey
        /// </summary>
        /// <param name="rowKey"></param>
        /// <returns></returns>
        public static string MatchRowKeyFilter(string rowKey)
        {
            return TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, rowKey);
        }

        /// <summary>
        /// Builds a query string that matches a specific partitionkey and rowkey.
        /// </summary>
        /// <param name="partitionKey"></param>
        /// <param name="rowKey"></param>
        /// <returns></returns>
        public static string MatchPartitionKeyAndRowKeyFilter(string partitionKey, string rowKey)
        {
            return TableQuery.CombineFilters(MatchPartitionKeyFilter(partitionKey), TableOperators.And,
                                      MatchRowKeyFilter(rowKey));
        }
    }
}
