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

using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.MembershipTests
{
    public class MembershipTablePluginTests
    {
        public TestContext TestContext { get; set; }
        private static string hostName = Dns.GetHostName();
        private static readonly TraceLogger logger = TraceLogger.GetLogger("MembershipTablePluginTests");

        internal static async Task MembershipTable_ReadAll_EmptyTable(IMembershipTable membership)
        {
            var data = await membership.ReadAll();
            Assert.IsNotNull(data, "Membership Data not null");

            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");
            Assert.IsNotNull(data.Version.VersionEtag, "ETag should not be null");
            Assert.AreEqual(0, data.Version.Version, "Initial tabel version should be zero");
        }

        internal static async Task MembershipTable_InsertRow(IMembershipTable membership)
        {
            var membershipEntry = CreateMembershipEntryForTest();

            var data = await membership.ReadAll();
            Assert.IsNotNull(data, "Membership Data not null");
            Assert.AreEqual(0, data.Members.Count, "Should be no data initially: {0}", data);

            bool ok = await membership.InsertRow(membershipEntry, data.Version.Next());
            Assert.IsTrue(ok, "InsertRow OK");

            data = await membership.ReadAll();
            Assert.AreEqual(1, data.Members.Count, "Should be one row after insert: {0}", data);
        }

        internal static async Task MembershipTable_ReadRow_Insert_Read(IMembershipTable membership)
        {
            MembershipTableData data = await membership.ReadAll();
            //TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            TableVersion newTableVersion = data.Version.Next();
            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membership.InsertRow(newEntry, newTableVersion);

            Assert.IsTrue(ok, "InsertRow completed successfully");

            data = await membership.ReadRow(newEntry.SiloAddress);
            logger.Info("Membership.ReadRow returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(1, data.Members.Count, "Number of records returned - data row only");

            Assert.IsNotNull(data.Version.VersionEtag, "New version ETag should not be null");
            Assert.AreNotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag, "New VersionEtag differetnfrom last");
            Assert.AreEqual(newTableVersion.Version, data.Version.Version, "New table version number");

            MembershipEntry MembershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadRow returned MembershipEntry ETag={0} Entry={1}", eTag, MembershipEntry);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(MembershipEntry, "MembershipEntry should not be null");
        }

        internal static async Task MembershipTable_ReadAll_Insert_ReadAll(IMembershipTable membership)
        {
            MembershipTableData data = await membership.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            TableVersion newTableVersion = data.Version.Next();
            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membership.InsertRow(newEntry, newTableVersion);

            Assert.IsTrue(ok, "InsertRow completed successfully");

            data = await membership.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(1, data.Members.Count, "Number of records returned - data row only");

            Assert.IsNotNull(data.Version.VersionEtag, "New version ETag should not be null");
            Assert.AreNotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag, "New VersionEtag differetnfrom last");
            Assert.AreEqual(newTableVersion.Version, data.Version.Version, "New table version number");

            MembershipEntry MembershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadAll returned MembershipEntry ETag={0} Entry={1}", eTag, MembershipEntry);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(MembershipEntry, "MembershipEntry should not be null");
        }

        internal static async Task MembershipTable_UpdateRow(IMembershipTable membership)
        {
            MembershipEntry data = CreateMembershipEntryForTest();

            MembershipTableData tableData = await membership.ReadAll();
            //TableVersion tableVer = tableData.Version;
            Assert.IsNotNull(tableData.Version, "TableVersion should not be null");
            Assert.AreEqual(0, tableData.Version.Version, "TableVersion should be zero");
            Assert.AreEqual(0, tableData.Members.Count, "Should be no data initially: {0}", tableData);

            TableVersion newTableVer = tableData.Version.Next();
            logger.Info("Calling InsertRow with Entry = {0} TableVersion = {1}", data, newTableVer);
            bool ok = await membership.InsertRow(data, newTableVer);

            Assert.IsTrue(ok, "InsertRow OK");

            tableData = await membership.ReadAll();
            Assert.IsNotNull(tableData.Version, "TableVersion should not be null");
            Assert.AreEqual(1, tableData.Version.Version, "TableVersion should be 1");
            Assert.AreEqual(1, tableData.Members.Count, "Should be one row after insert: {0}", tableData);

            Tuple<MembershipEntry, string> insertedData = tableData.Get(data.SiloAddress);
            Assert.IsNotNull(insertedData.Item2, "ETag should not be null");
            insertedData.Item1.Status = SiloStatus.Active;

            newTableVer = tableData.Version.Next();

            logger.Info("Calling UpdateRow with Entry = {0} eTag = {1} New TableVersion={2}", insertedData.Item1, insertedData.Item2, newTableVer);
            ok = await membership.UpdateRow(insertedData.Item1, insertedData.Item2, newTableVer);

            tableData = await membership.ReadAll();
            Assert.IsNotNull(tableData.Version, "TableVersion should not be null");
            Assert.AreEqual(2, tableData.Version.Version, "TableVersion should be 2");
            Assert.AreEqual(1, tableData.Members.Count, "Should be one row after insert: {0}", tableData);

            Assert.IsTrue(ok, "UpdateRow OK - Table Data = {0}", tableData);
        }

        // Utility methods
        private static MembershipEntry CreateMembershipEntryForTest()
        {
            var siloAddress = SiloAddress.NewLocalAddress(SiloAddress.AllocateNewGeneration());

            var now = DateTime.UtcNow;
            var membershipEntry = new MembershipEntry
            {
                SiloAddress = siloAddress,
                HostName = hostName,
                RoleName = hostName,
                InstanceName = hostName,
                Status = SiloStatus.Joining,
                StartTime = now,
                IAmAliveTime = now
            };

            return membershipEntry;
        }

        private static MembershipEntry CreateActiveMembershipEntryForTest(SiloAddress siloAddress)
        {
            DateTime now = DateTime.UtcNow;
            return new MembershipEntry
            {
                SiloAddress = siloAddress,
                HostName = "TestHost",
                RoleName = "TestRole",
                InstanceName = "TestInstance",
                Status = SiloStatus.Active,
                StartTime = now,
            };
        }
    }
}
