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
using System.Threading.Tasks;
using Orleans.SqlUtils;


namespace Orleans.Runtime.ReminderService
{
    internal class SqlReminderTable: IReminderTable
    {
        private string serviceId;
        private string deploymentId;
        private RelationalOrleansQueries orleansQueries;

        public async Task Init(GlobalConfiguration config, TraceLogger logger)
        {
            serviceId = config.ServiceId.ToString();
            deploymentId = config.DeploymentId;
            orleansQueries = await RelationalOrleansQueries.CreateInstance(config.AdoInvariantForReminders, config.DataConnectionStringForReminders);
        }


        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            return orleansQueries.ReadReminderRowsAsync(serviceId, grainRef);
        }


        public Task<ReminderTableData> ReadRows(uint beginHash, uint endHash)
        {
            return orleansQueries.ReadReminderRowsAsync(serviceId, beginHash, endHash);
        }


        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            return orleansQueries.ReadReminderRowAsync(serviceId, grainRef, reminderName);
        }   
        
        public Task<string> UpsertRow(ReminderEntry entry)
        {
            return orleansQueries.UpsertReminderRowAsync(serviceId, entry.GrainRef, entry.ReminderName, entry.StartAt, entry.Period);            
        }

        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            return orleansQueries.DeleteReminderRowAsync(serviceId, grainRef, reminderName, eTag);            
        }


        public Task TestOnlyClearTable()
        {
            return orleansQueries.DeleteReminderRowsAsync(serviceId);
        }
    }
}
