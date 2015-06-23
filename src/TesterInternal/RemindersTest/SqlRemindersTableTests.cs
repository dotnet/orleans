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
using NUnit.Framework;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;
using Orleans.Runtime.Storage.Relational;
using UnitTests.StorageTests;
using UnitTests.General;

namespace UnitTests.RemindersTest
{
    /// <summary>
    /// Tests for operation of Orleans Reminders Table using SQL
    /// </summary>
    [TestFixture]
    public class SQLRemindersTableTests
    {
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
        [TestFixtureSetUp]
        public static void ClassInitialize()
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
                                             DataConnectionString = relationalStorage.ConnectionString
                                         };

            var rmndr = new SqlReminderTable(config);
            await rmndr.Init(serviceId, deploymentId, config.DataConnectionString).WithTimeout(timeout);
            reminder = rmndr;
        }


        // Use TestCleanup to run code after each test has run
        [TearDown]
        public void TestCleanup()
        {
            if (reminder != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                reminder.TestOnlyClearTable().Wait();
                reminder = null;
            }
            var testContext = TestContext.CurrentContext;

            logger.Info("Test {0} completed - Outcome = {1}", testContext.Test.Name, testContext.Result.Status);
        }


        [TestFixtureTearDown]
        public static void ClassCleanup()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
        }


        [Test, Category("Reminders"), Category("SqlServer")]
        public async Task RemindersTable_SqlServer_Init()
        {
            await Initialize();
            Assert.IsNotNull(reminder, "Reminder Table handler created");
        }


        [Test, Category("Reminders"), Category("SqlServer")]
        public async Task RemindersTable_SqlServer_UpsertReminderTwice()
        {
            await Initialize();
            await ReminderTablePluginTests.ReminderTableUpsertTwice(reminder);
        }
    }
}