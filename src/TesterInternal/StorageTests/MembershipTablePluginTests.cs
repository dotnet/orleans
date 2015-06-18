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
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;

namespace UnitTests.StorageTests
{
    public class MembershipTablePluginTests
    {
        public TestContext TestContext { get; set; }
        private static string hostName = Dns.GetHostName();
        private static readonly TraceLogger logger = TraceLogger.GetLogger("MembershipTablePluginTests");

        internal static async Task MembershipTable_ReadAll(IMembershipTable membership)
        {
            var membershipData = await membership.ReadAll();
            Assert.IsNotNull(membershipData, "Membership Data not null");

            TableVersion tableVersion = membershipData.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", tableVersion, membershipData);

            Assert.AreEqual(0, membershipData.Members.Count, "Number of records returned - no table version row");

            string eTag = tableVersion.VersionEtag;
            int ver = tableVersion.Version;

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.AreEqual(0, ver, "Initial tabel version should be zero");
        }

        internal static async Task MembershipTable_InsertRow(IMembershipTable membership)
        {
            var membershipEntry = CreateMembershipEntryForTest();

            var membershipData = await membership.ReadAll();
            Assert.IsNotNull(membershipData, "Membership Data not null");
            Assert.AreEqual(0, membershipData.Members.Count, "Should be no data initially: {0}", membershipData);

            bool ok = await membership.InsertRow(membershipEntry, membershipData.Version);
            Assert.IsTrue(ok, "InsertRow OK");

            membershipData = await membership.ReadAll();
            Assert.AreEqual(1, membershipData.Members.Count, "Should be one row after insert: {0}", membershipData);
        }

        internal static async Task MembershipTable_ReadRow_EmptyTable(IMembershipTable membership, SiloAddress siloAddress)
        {
            MembershipTableData data = await membership.ReadRow(siloAddress);
            TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadRow returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            string eTag = tableVersion.VersionEtag;
            int ver = tableVersion.Version;

            logger.Info("Membership.ReadRow returned MembershipEntry ETag={0} TableVersion={1}", eTag, tableVersion);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.AreEqual(0, ver, "Initial table version should be zero");
        }

        internal static async Task MembershipTable_ReadRow_Insert_Read(IMembershipTable membership, SiloAddress siloAddress)
        {
            MembershipTableData data = await membership.ReadAll();
            TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            DateTime now = DateTime.UtcNow;
            MembershipEntry entry = new MembershipEntry
            {
                SiloAddress = siloAddress,
                StartTime = now,
                Status = SiloStatus.Active,
            };

            TableVersion newTableVersion = tableVersion.Next();
            bool ok = await membership.InsertRow(entry, newTableVersion);

            Assert.IsTrue(ok, "InsertRow completed successfully");

            data = await membership.ReadRow(siloAddress);
            tableVersion = data.Version;
            logger.Info("Membership.ReadRow returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(1, data.Members.Count, "Number of records returned - data row only");

            Assert.IsNotNull(tableVersion.VersionEtag, "New version ETag should not be null");
            Assert.AreNotEqual(newTableVersion.VersionEtag, tableVersion.VersionEtag, "New VersionEtag differetnfrom last");
            Assert.AreEqual(newTableVersion.Version, tableVersion.Version, "New table version number");

            MembershipEntry MembershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadRow returned MembershipEntry ETag={0} Entry={1}", eTag, MembershipEntry);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(MembershipEntry, "MembershipEntry should not be null");
        }

        internal static async Task MembershipTable_ReadAll_Insert_ReadAll(IMembershipTable membership, SiloAddress siloAddress)
        {
            MembershipTableData data = await membership.ReadAll();
            TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            DateTime now = DateTime.UtcNow;
            MembershipEntry entry = new MembershipEntry
            {
                SiloAddress = siloAddress,
                StartTime = now,
                Status = SiloStatus.Active,
            };

            TableVersion newTableVersion = tableVersion.Next();
            bool ok = await membership.InsertRow(entry, newTableVersion);

            Assert.IsTrue(ok, "InsertRow completed successfully");

            data = await membership.ReadAll();
            tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(1, data.Members.Count, "Number of records returned - data row only");

            Assert.IsNotNull(tableVersion.VersionEtag, "New version ETag should not be null");
            Assert.AreNotEqual(newTableVersion.VersionEtag, tableVersion.VersionEtag, "New VersionEtag differetnfrom last");
            Assert.AreEqual(newTableVersion.Version, tableVersion.Version, "New table version number");

            MembershipEntry MembershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadAll returned MembershipEntry ETag={0} Entry={1}", eTag, MembershipEntry);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(MembershipEntry, "MembershipEntry should not be null");
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
    }
}
