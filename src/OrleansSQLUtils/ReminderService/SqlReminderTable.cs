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

using Orleans.Runtime.Configuration;
using System;
using System.Threading.Tasks;
using Orleans.SqlUtils;
using Orleans.SqlUtils.Management;


namespace Orleans.Runtime.ReminderService
{
    internal class SqlReminderTable: IReminderTable
    {
        private string serviceId;
        private string deploymentId;
        private IRelationalStorage database;
        private QueryConstantsBag queryConstants;

        public async Task Init(GlobalConfiguration config, TraceLogger logger)
        {
            serviceId = config.ServiceId.ToString();
            deploymentId = config.DeploymentId;
            database = RelationalStorageUtilities.CreateGenericStorageInstance(config.AdoInvariantForReminders,
                config.DataConnectionStringForReminders);
            queryConstants = await database.InitializeOrleansQueriesAsync();
        }


        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.ReadReminderRowsKey);
            return database.ReadReminderRowsAsync(query, serviceId, grainRef);
        }


        public Task<ReminderTableData> ReadRows(uint beginHash, uint endHash)
        {
            var queryKey = beginHash < endHash ? QueryKeys.ReadRangeRows1Key : QueryKeys.ReadRangeRows2Key;
            var query = queryConstants.GetConstant(database.InvariantName, queryKey);
            return database.ReadReminderRowsAsync(query, serviceId, beginHash, endHash);
        }


        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.ReadReminderRowKey);
            return database.ReadReminderRowAsync(query, serviceId, grainRef, reminderName);
        }
              
        
        public Task<string> UpsertRow(ReminderEntry entry)
        {
            var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.UpsertReminderRowKey);
            return database.UpsertReminderRowAsync(query, serviceId, entry.GrainRef, entry.ReminderName, entry.StartAt, entry.Period);            
        }


        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.DeleteReminderRowKey);
            return database.DeleteReminderRowAsync(query, serviceId, grainRef, reminderName, eTag);            
        }


        public Task TestOnlyClearTable()
        {
            var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.DeleteReminderRowsKey);
            return database.DeleteReminderRowsAsync(query, serviceId);
        }
    }
}
