using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.RemindersTest
{
    internal class ReminderTablePluginTests
    {
        internal static async Task ReminderTableUpsertParallel(IReminderTable reminder)
        {
            var grainRef = MakeTestGrainReference();
            var period = TimeSpan.FromMinutes(1);
            var reminderName = "testReminderName";
            var startAt = DateTime.UtcNow.Add(TimeSpan.FromMinutes(1));
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(x => UpsertReminder(reminder, grainRef, reminderName, startAt, period)));

            Assert.IsTrue(results.Distinct().Count() == results.Length);
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
