using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.RemindersTest
{
    internal class ReminderTablePluginTests
    {
        public TestContext TestContext { get; set; }
        private static readonly TraceLogger logger = TraceLogger.GetLogger("ReminderTablePluginTests");

        internal static async Task ReminderTableUpsertTwice(IReminderTable reminder)
        {
            var grainRef = MakeTestGrainReference();
            var period = TimeSpan.FromMinutes(1);
            var reminderName = "testReminderName";
            var startAt = DateTime.UtcNow.Add(TimeSpan.FromMinutes(1));
            var etag1 = await UpsertReminder(reminder, grainRef, reminderName, startAt, period);
            var etag2 = await UpsertReminder(reminder, grainRef, reminderName, startAt, period);
            Assert.AreNotEqual(etag1, etag2);
        }

        private static async Task<string> UpsertReminder(IReminderTable reminder, GrainReference grainRef, string reminderName, DateTime startAt, TimeSpan period)
        {
            var reminderRow = new ReminderEntry
                              {
                                  GrainRef = grainRef,
                                  Period = period,
                                  StartAt = startAt,
                                  ReminderName = reminderName
                              };
            return await reminder.UpsertRow(reminderRow);
        }

        private static GrainReference MakeTestGrainReference()
        {
            Guid guid = Guid.NewGuid();
            GrainId regularGrainId = GrainId.GetGrainIdForTesting(guid);
            GrainReference grainRef = GrainReference.FromGrainId(regularGrainId);
            return grainRef;
        }
    }
}
