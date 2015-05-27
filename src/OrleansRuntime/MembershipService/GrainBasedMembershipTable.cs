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

using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime.MembershipService
{
    [Reentrant]
    internal class GrainBasedMembershipTable : Grain, IMembershipTable
    {
        private InMemoryMembershipTable table;
        private TraceLogger logger;

        public override Task OnActivateAsync()
        {
            logger = TraceLogger.GetLogger("GrainBasedMembershipTable", TraceLogger.LoggerType.Runtime);
            logger.Info(ErrorCode.MembershipGrainBasedTable1, "GrainBasedMembershipTable Activated.");
            table = new InMemoryMembershipTable();
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("GrainBasedMembershipTable Deactivated.");
            return TaskDone.Done;
        }

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            return Task.FromResult(table.Read(key));
        }

        public Task<MembershipTableData> ReadAll()
        {
            var t = table.ReadAll();
            return Task.FromResult(t);
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            if (logger.IsVerbose) logger.Verbose("InsertRow entry = {0}, table version = {1}", entry.ToFullString(), tableVersion);
            bool result = table.Insert(entry, tableVersion);
            if (result == false)
                logger.Info(ErrorCode.MembershipGrainBasedTable2, 
                    "Insert of {0} and table version {1} failed. Table now is {2}",
                    entry.ToFullString(), tableVersion, table.ReadAll());

            return Task.FromResult(result);
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (logger.IsVerbose) logger.Verbose("UpdateRow entry = {0}, etag = {1}, table version = {2}", entry.ToFullString(), etag, tableVersion);
            bool result = table.Update(entry, etag, tableVersion);
            if (result == false)
                logger.Info(ErrorCode.MembershipGrainBasedTable3,
                    "Update of {0}, eTag {1}, table version {2} failed. Table now is {3}",
                    entry.ToFullString(), etag, tableVersion, table.ReadAll());

            return Task.FromResult(result);
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            if (logger.IsVerbose) logger.Verbose("UpdateIAmAlive entry = {0}", entry.ToFullString());
            table.UpdateIAmAlive(entry);
            return TaskDone.Done;
        }
    }
}

