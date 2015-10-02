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