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
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;
using Orleans.SqlUtils;
using UnitTests.StorageTests;
using UnitTests.General;
using System.Xml;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using SQL
    /// </summary>
    [TestClass]
    [DeploymentItem("CreateOrleansTables_SqlServer.sql")]
    public class SQLRemindersTableTests
    {
        public TestContext TestContext { get; set; }

        private string deploymentId;
        private SiloAddress siloAddress;
        private static IRelationalStorage relationalStorage;
        private const string testDatabaseName = "OrleansTest";
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);

        private readonly TraceLogger logger = TraceLogger.GetLogger("SQLReminderTableTests",
            TraceLogger.LoggerType.Application);

        private Guid serviceId;
        private SqlReminderTable reminder;

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            TraceLogger.Initialize(new NodeConfiguration());
            TraceLogger.AddTraceLevelOverride("SQLReminderTableTests", Logger.Severity.Verbose3);

            // Set shorter init timeout for these tests
            OrleansSiloInstanceManager.initTimeout = TimeSpan.FromSeconds(20);
            relationalStorage = SqlTestsEnvironment.Setup(testDatabaseName);
        }


        private async Task Initialize()
        {
            serviceId = Guid.NewGuid();
            deploymentId = "test-" + Guid.NewGuid();
            int generation = SiloAddress.AllocateNewGeneration();
            siloAddress = SiloAddress.NewLocalAddress(generation);

            logger.Info("DeploymentId={0} Generation={1}", deploymentId, generation);

            GlobalConfiguration config = new GlobalConfiguration
                                         {
                                             DeploymentId = deploymentId,
                                             DataConnectionStringForReminders = relationalStorage.ConnectionString,
                                             AdoInvariantForReminders =  relationalStorage.InvariantName
                                         };

            var rmndr = new SqlReminderTable();
            await rmndr.Init(config, logger).WithTimeout(timeout);
            reminder = rmndr;
        }


        // Use TestCleanup to run code after each test has run
        [TestCleanup]
        public void TestCleanup()
        {
            if (reminder != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                reminder.TestOnlyClearTable().Wait();
                reminder = null;
            }
            logger.Info("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }


        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
        }


        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_Init()
        {
            await Initialize();
            Assert.IsNotNull(reminder, "Reminder Table handler created");
        }


        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public async Task RemindersTable_SqlServer_UpsertReminderTwice()
        {
            await Initialize();
            await ReminderTablePluginTests.ReminderTableUpsertTwice(reminder);
        }

        #region sampleSpecificMembershipAndRemidersTableConfiguration
        private readonly string sampleSpecificMembershipAndRemidersTableConfiguration = @"<?xml version=""1.0"" encoding=""utf-8""?>
<OrleansConfiguration xmlns = ""urn:orleans"" >
  <Globals >
    <StorageProviders >
      <Provider Type=""Orleans.Storage.MemoryStorage"" Name=""MemoryStore"" />
    </StorageProviders>
    <SeedNode Address = ""localhost"" Port=""11111"" />
    <SystemStore SystemStoreType = ""Custom""  DataConnectionString=""MembershipConnectionString""
             MembershipTableAssembly=""MembershipTableDLL""
             ReminderTableAssembly=""RemindersTableDLL""
             DataConnectionStringForReminders=""RemindersConnectionString""
             AdoInvariant=""AdoInvariantValue""
             AdoInvariantForReminders=""AdoInvariantForReminders""
                 />
  </Globals>
  <Defaults>
    <Networking Address = ""localhost"" Port=""11111"" />
    <ProxyingGateway Address = ""localhost"" Port=""30000"" />
    <Tracing DefaultTraceLevel = ""Info"" TraceToConsole=""true"" TraceToFile=""{0}-{1}.log"">
      <TraceLevelOverride LogPrefix = ""Application"" TraceLevel=""Info"" />
    </Tracing>
    <Statistics MetricsTableWriteInterval = ""30s"" PerfCounterWriteInterval=""30s"" LogWriteInterval=""300s"" WriteLogStatisticsToTable=""true"" />
  </Defaults>
  <Override Node = ""Primary"" >
    <Networking Address=""localhost"" Port=""11111"" />
    <ProxyingGateway Address = ""localhost"" Port=""30000"" />
  </Override>
</OrleansConfiguration>";
#endregion
        [TestMethod, TestCategory("Reminders"), TestCategory("SqlServer")]
        public void RemindersTable_SqlServer_Can_Have_Different_Reminders_And_Membership_Settings_ViaXml()
        {
            var config = new ClusterConfiguration();
            var doc = new XmlDocument();
            doc.LoadXml(sampleSpecificMembershipAndRemidersTableConfiguration);
            config.LoadFromXml(doc.DocumentElement);
            Assert.IsTrue(config.Globals.MembershipTableAssembly == "MembershipTableDLL");
            Assert.IsTrue(config.Globals.ReminderTableAssembly == "RemindersTableDLL");
            Assert.IsTrue(config.Globals.AdoInvariant == "AdoInvariantValue");
            Assert.IsTrue(config.Globals.AdoInvariantForReminders == "AdoInvariantForReminders");
            Assert.IsTrue(config.Globals.DataConnectionString == "MembershipConnectionString");
            Assert.IsTrue(config.Globals.DataConnectionStringForReminders == "RemindersConnectionString");
        }
    }
}