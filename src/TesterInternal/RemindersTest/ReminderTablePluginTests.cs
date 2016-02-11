using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.RemindersTest
{
    internal class ReminderTablePluginTests
    {
        internal static async Task ReminderTableTest(IReminderTable reminderTable)
        {
            Guid guid = Guid.NewGuid();
            var results = await Task.WhenAll(Enumerable.Range(0, 10).
                Select(x => reminderTable.UpsertRow(CreateReminder(MakeTestGrainReference(guid), "0"))));

            Assert.AreEqual(results.Distinct().Count(), results.Length);

            await Task.WhenAll(Enumerable.Range(1, 999).Select(async i =>
            {
                GrainReference grainRef = MakeTestGrainReference(Guid.NewGuid());
                await reminderTable.UpsertRow(CreateReminder(grainRef, i.ToString()));
            }));

            var rows = await reminderTable.ReadRows(0, uint.MaxValue);

            Assert.AreEqual(rows.Reminders.Count, 1000);

            rows = await reminderTable.ReadRows(0, 0);

            Assert.AreEqual(rows.Reminders.Count, 1000);

            var remindersHashes = rows.Reminders.Select(r => r.GrainRef.GetUniformHashCode()).ToArray();
            
            SafeRandom random = new SafeRandom();

            await Task.WhenAll(Enumerable.Repeat(
                        TestRemindersHashInterval(reminderTable, (uint) random.Next(), (uint) random.Next(),
                            remindersHashes), 1000));

            var reminder = rows.Reminders.First();

            var shouldExist = await reminderTable.ReadRow(reminder.GrainRef, reminder.ReminderName);

            Assert.IsNotNull(shouldExist);

            string etagTemp = reminder.ETag;

            reminder.ETag = await reminderTable.UpsertRow(reminder);

            var removeRowRes = await reminderTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, etagTemp);
            Assert.IsFalse(removeRowRes, "should have failed. Etag is wrong");
            removeRowRes = await reminderTable.RemoveRow(reminder.GrainRef, "bla", reminder.ETag);
            Assert.IsFalse(removeRowRes, "should have failed. reminder name is wrong");
            removeRowRes = await reminderTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, reminder.ETag);
            Assert.IsTrue(removeRowRes, "should have succeeded. Etag is right");
            removeRowRes = await reminderTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, reminder.ETag);
            Assert.IsFalse(removeRowRes, "should have failed. reminder shouldn't exist");
        }

        private static async Task TestRemindersHashInterval(IReminderTable reminderTable, uint beginHash, uint endHash,
            uint[] remindersHashes)
        {
            var rowsTask = reminderTable.ReadRows(beginHash, endHash);
            var expectedHashes = beginHash < endHash
                ? remindersHashes.Where(r => r > beginHash && r <= endHash)
                : remindersHashes.Where(r => r > beginHash || r <= endHash);

            HashSet<uint> expectedSet = new HashSet<uint>(expectedHashes);
            var returnedHashes = (await rowsTask).Reminders.Select(r => r.GrainRef.GetUniformHashCode());
            var returnedSet = new HashSet<uint>(returnedHashes);

            Assert.IsTrue(returnedSet.SetEquals(expectedSet));
        }

        private static ReminderEntry CreateReminder(GrainReference grainRef, string reminderName)
        {
            return new ReminderEntry
            {
                GrainRef = grainRef,
                Period = TimeSpan.FromMinutes(1),
                StartAt = DateTime.UtcNow.Add(TimeSpan.FromMinutes(1)),
                ReminderName = reminderName
            };
        }

        private static GrainReference MakeTestGrainReference(Guid guid)
        {
            GrainId regularGrainId = GrainId.GetGrainIdForTesting(guid);
            GrainReference grainRef = GrainReference.FromGrainId(regularGrainId);
            return grainRef;
        }
    }
}
