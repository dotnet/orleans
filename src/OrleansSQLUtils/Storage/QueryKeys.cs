/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// Keys to operational queries Orleans uses to operate a given relational database.
    /// These keys point to queries loaded from the database when starting up.
    /// </summary>
    /// <remarks>These keys point to the only queries Orleans ever issues to the database.
    /// See file <em>CreateOrleansTables_SqlServer.sql</em> for further implementation details.</remarks>
    public static class QueryKeys
    {
        /// <summary>
        /// This key defines the query to retrieve Orleans operational queries
        /// and fill in the queries matching the other keys.
        /// </summary>
        public const string OrleansQueriesKey = "OrleansQueriesKey";

        /// <summary>
        /// A key for a query template to retrieve gateway URIs.
        /// </summary>        
        public const string ActiveGatewaysQuery = "ActiveGatewaysQueryKey";

        /// <summary>
        /// A key for a query template to retrieve a single row of membership data.
        /// </summary>        
        public const string MembershipReadRowKey = "MembershipReadRowKey";

        /// <summary>
        /// A key for a query template to retrieve all membership data.
        /// </summary>        
        public const string MembershipReadAllKey = "MembershipReadAllKey";

        /// <summary>
        /// A key for a query to insert a membership version row.
        /// </summary>
        public const string InsertMembershipVersionKey = "InsertMembershipVersionKey";

        /// <summary>
        /// A key for a query to update "I Am Alive Time".
        /// </summary>
        public const string UpdateIAmAlivetimeKey = "UpdateIAmAlivetimeKey";

        /// <summary>
        /// A key for a query to insert a membership row.
        /// </summary>
        public const string InsertMembershipKey = "InsertMembershipKey";

        /// <summary>
        /// A key for a query to update a membership row.
        /// </summary>
        public const string UpdateMembershipKey = "UpdateMembershipKey";

        /// <summary>
        /// A key for a query to delete membersip entries.
        /// </summary>
        public const string DeleteMembershipTableEntriesKey = "DeleteMembershipTableEntriesKey";

        /// <summary>
        /// A key for a query to read reminder entries.
        /// </summary>
        public const string ReadReminderRowsKey = "ReadReminderRowsKey";

        /// <summary>
        /// A key for a query to read reminder entries with ranges.
        /// </summary>
        public const string ReadRangeRows1Key = "ReadRangeRows1Key";

        /// <summary>
        /// A key for a query to read reminder entries with ranges.
        /// </summary>
        public const string ReadRangeRows2Key = "ReadRangeRows2Key";

        /// <summary>
        /// A key for a query to read a reminder entry with ranges.
        /// </summary>
        public const string ReadReminderRowKey = "ReadReminderRowKey";

        /// <summary>
        /// A key for a query to upsert a reminder row.
        /// </summary>
        public const string UpsertReminderRowKey = "UpsertReminderRowKey";

        /// <summary>
        /// A key for a query to insert Orleans statistics.
        /// </summary>
        public const string InsertOrleansStatisticsKey = "InsertOrleansStatisticsKey";

        /// <summary>
        /// A key for a query to insert or update an Orleans client metrics key.
        /// </summary>
        public const string UpsertReportClientMetricsKey = "UpsertReportClientMetricsKey";

        /// <summary>
        /// A key for a query to insert or update an Orleans silo metrics key.
        /// </summary>
        public const string UpsertSiloMetricsKey = "UpsertSiloMetricsKey";

        /// <summary>
        /// A key for a query to delete a reminder row.
        /// </summary>
        public const string DeleteReminderRowKey = "DeleteReminderRowKey";

        /// <summary>
        /// A key for a query to delete all reminder rows.
        /// </summary>
        public const string DeleteReminderRowsKey = "DeleteReminderRowsKey";

        /// <summary>
        /// A collection of the well known keys Orleans uses to retrieve operational database queries.
        /// </summary>
        public static readonly IReadOnlyCollection<string> Keys = new ReadOnlyCollection<string>(new[]
        {
            OrleansQueriesKey,
            ActiveGatewaysQuery,
            MembershipReadRowKey,
            MembershipReadAllKey,
            UpdateIAmAlivetimeKey,
            InsertMembershipVersionKey,
            InsertMembershipKey,
            UpdateMembershipKey,
            DeleteMembershipTableEntriesKey,
            ReadReminderRowsKey,
            ReadRangeRows1Key,
            ReadRangeRows2Key,
            ReadReminderRowKey,
            InsertOrleansStatisticsKey,
            UpsertReminderRowKey,
            UpsertReportClientMetricsKey,
            UpsertSiloMetricsKey,
            DeleteReminderRowKey,
            DeleteReminderRowsKey
        });
    }
}
