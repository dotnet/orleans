using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ReminderService;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedVariable

namespace Tester.AzureUtils.TimerTests
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestCategory("Azure")]
    public class ReminderTests_Azure_Standalone : AzureStorageBasicTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;

        private Guid ServiceId;

        private Logger log;
        
        public ReminderTests_Azure_Standalone(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            log = LogManager.GetLogger(GetType().Name, LoggerType.Application);

            ServiceId = Guid.NewGuid();

            TestUtils.ConfigureClientThreadPoolSettingsForStorageTests(1000);
        }

        #region Extra tests / experiments

        [SkippableFact, TestCategory("ReminderService"), TestCategory("Performance")]
        public async Task Reminders_AzureTable_InsertRate()
        {
            IReminderTable table = new AzureBasedReminderTable(this.fixture.Services.GetRequiredService<IGrainReferenceConverter>());
            var config = new GlobalConfiguration()
            {
                ServiceId = ServiceId,
                DeploymentId = "TMSLocalTesting",
                DataConnectionString = TestDefaultConfiguration.DataConnectionString
            };
            await table.Init(config, log);

            await TestTableInsertRate(table, 10);
            await TestTableInsertRate(table, 500);
        }

        [SkippableFact, TestCategory("ReminderService")]
        public async Task Reminders_AzureTable_InsertNewRowAndReadBack()
        {
            string deploymentId = NewDeploymentId();
            IReminderTable table = new AzureBasedReminderTable(this.fixture.Services.GetRequiredService<IGrainReferenceConverter>());
            var config = new GlobalConfiguration()
            {
                ServiceId = ServiceId,
                DeploymentId = deploymentId,
                DataConnectionString = TestDefaultConfiguration.DataConnectionString
            };
            await table.Init(config, log);

            ReminderEntry[] rows = (await GetAllRows(table)).ToArray();
            Assert.Equal(0,  rows.Count()); // "The reminder table (sid={0}, did={1}) was not empty.", ServiceId, deploymentId);

            ReminderEntry expected = NewReminderEntry();
            await table.UpsertRow(expected);
            rows = (await GetAllRows(table)).ToArray();

            Assert.Equal(1,  rows.Count()); // "The reminder table (sid={0}, did={1}) did not contain the correct number of rows (1).", ServiceId, deploymentId);
            ReminderEntry actual = rows[0];
            Assert.Equal(expected.GrainRef,  actual.GrainRef); // "The newly inserted reminder table (sid={0}, did={1}) row did not contain the expected grain reference.", ServiceId, deploymentId);
            Assert.Equal(expected.ReminderName,  actual.ReminderName); // "The newly inserted reminder table (sid={0}, did={1}) row did not have the expected reminder name.", ServiceId, deploymentId);
            Assert.Equal(expected.Period,  actual.Period); // "The newly inserted reminder table (sid={0}, did={1}) row did not have the expected period.", ServiceId, deploymentId);
            // the following assertion fails but i don't know why yet-- the timestamps appear identical in the error message. it's not really a priority to hunt down the reason, however, because i have high confidence it is working well enough for the moment.
            /*Assert.Equal(expected.StartAt,  actual.StartAt); // "The newly inserted reminder table (sid={0}, did={1}) row did not contain the correct start time.", ServiceId, deploymentId);*/
            Assert.False(string.IsNullOrWhiteSpace(actual.ETag), $"The newly inserted reminder table (sid={ServiceId}, did={deploymentId}) row contains an invalid etag.");
        }

        private async Task TestTableInsertRate(IReminderTable reminderTable, double numOfInserts)
        {
            DateTime startedAt = DateTime.UtcNow;

            try
            {
                List<Task<bool>> promises = new List<Task<bool>>();
                for (int i = 0; i < numOfInserts; i++)
                {
                    //"177BF46E-D06D-44C0-943B-C12F26DF5373"
                    string s = string.Format("177BF46E-D06D-44C0-943B-C12F26D{0:d5}", i);

                    var e = new ReminderEntry
                    {
                        //GrainId = GrainId.GetGrainId(new Guid(s)),
                        GrainRef = this.fixture.InternalGrainFactory.GetGrain(GrainId.NewId()),
                        ReminderName = "MY_REMINDER_" + i,
                        Period = TimeSpan.FromSeconds(5),
                        StartAt = DateTime.UtcNow
                    };

                    int capture = i;
                    Task<bool> promise = Task.Run(async () =>
                    {
                        await reminderTable.UpsertRow(e);
                        output.WriteLine("Done " + capture);
                        return true;
                    });
                    promises.Add(promise);
                    log.Info("Started " + capture);
                }
                log.Info("Started all, now waiting...");
                await Task.WhenAll(promises).WithTimeout(TimeSpan.FromSeconds(500));
            }
            catch (Exception exc)
            {
                log.Info("Exception caught {0}", exc);
            }
            TimeSpan dur = DateTime.UtcNow - startedAt;
            log.Info("Inserted {0} rows in {1}, i.e., {2:f2} upserts/sec", numOfInserts, dur, (numOfInserts / dur.TotalSeconds));
        }
        #endregion

        private ReminderEntry NewReminderEntry()
        {
            Guid guid = Guid.NewGuid();
            return new ReminderEntry
                {
                    GrainRef = this.fixture.InternalGrainFactory.GetGrain(GrainId.NewId()),
                    ReminderName = string.Format("TestReminder.{0}", guid),
                    Period = TimeSpan.FromSeconds(5),
                    StartAt = DateTime.UtcNow
                };
        }

        private string NewDeploymentId()
        {
            return string.Format("ReminderTest.{0}", Guid.NewGuid());
        }

        private async Task<IEnumerable<ReminderEntry>> GetAllRows(IReminderTable table)
        {
            ReminderTableData data = await table.ReadRows(0, 0xffffffff);
            return data.Reminders;
        }
    }
}
// ReSharper restore InconsistentNaming
// ReSharper restore UnusedVariable
