using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Runtime.Storage;
using Orleans.Storage;
using Orleans.Runtime;
using Xunit;
using Orleans;
using System.Threading;

namespace EventSourcing.Tests
{
    /// <summary>
    /// Common tests for event stores.
    /// To use these tests, derive from this class, and specify 
    /// the store to test via the abstract property.
    /// All tests create fresh GUIDs, so no cleaning is necessary in between tests.
    /// </summary>
    public abstract class EventStoreTests : OrleansTestingBase
    {
        protected abstract IEventStorage StoreUnderTest { get; }

        private IDisposable Init()
        {
            stream = StoreUnderTest.GetEventStreamHandle(Guid.NewGuid().ToString());
            expected = new List<KeyValuePair<Guid, String>>();
            return stream;
        }

        IEventStreamHandle stream;
        List<KeyValuePair<Guid, String>> expected;


        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendOne()
        {
            using (Init())
            {
                await Append(1);
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendOneExpectPosition()
        {
            using (Init())
            {
                await Append(1, 0);
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendTwoExpectWrongPosition()
        {
            using (Init())
            {
                await Append(1, 0);
                await Append(1, 0); // fails
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendFive()
        {
            using (Init())
            {
                await Append(1, 0);
                await Append(1);
                await Append(1);
                await Append(1, 3);
                await Append(1, 3); //fails
                await Append(1);
                await CheckBunchOfQueries(5);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendOneOneThree()
        {
            using (Init())
            {
                await Append(1, 0);
                await Append(1);
                await Append(3, 2);
                await CheckBunchOfQueries(5);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Append500()
        {
            using (Init())
            {
                await Append(500, 0);
                await CheckBunchOfQueries(500);
                await Delete(500);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendLots()
        {
            using (Init())
            {
                await Append(100, 0);
                await Append(1000, 100);
                await Append(10000, 1100);
                await CheckBunchOfQueries(11100);
                await Delete(11100);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task EmptyStart()
        {
            using (Init())
            {
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendEmptyRange()
        {
            using (Init())
            {
                await Append(0); // really does nothing, appends zero events
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task FailedAppend()
        {
            using (Init())
            {
                await Append(1, 1); // fails, thus does not append anything
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendOneThenDelete()
        {
            using (Init())
            {
                await Append(1);
                await Delete();
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task AppendTenThenDelete()
        {
            using (Init())
            {
                await Append(10);
                await Delete(10);
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task CheckRepeatedDeletes()
        {
            using (Init())
            {
                await Append(4);
                await Delete(10); // fails
                await CheckBunchOfQueries();
                await Delete(4); // succeeds
                await CheckBunchOfQueries();
                await Delete(4); // fails
                await CheckBunchOfQueries();
                await Delete(0); // suceeds
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task DeleteNonexistent()
        {
            using (Init())
            {
                await Delete(0); // suceeds - model makes no difference between empty stream and nonexistent stream
                await CheckBunchOfQueries();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task ConcurrentAppend()
        {
            using (Init())
            {

                int countsuccesses = 0;
                Func<int, int, Task> append = async (int count, int expVersion) =>
                 {
                     var evts = new KeyValuePair<Guid, object>[count];
                     for (int i = 0; i < count; i++)
                         evts[i] = new KeyValuePair<Guid, object>(Guid.NewGuid(), (expected.Count + i).ToString());
                     var result = await stream.Append(evts, expVersion);
                     if (result)
                         Interlocked.Increment(ref countsuccesses);
                 };


                Parallel.Invoke(
                   () => append(200, 0).Wait(),
                   () => append(200, 0).Wait(),
                   () => append(200, 0).Wait(),
                   () => append(200, 0).Wait()
                );

                Assert.Equal(countsuccesses, 1);
                Assert.Equal(200, await stream.GetVersion());
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task IdempotentAppend()
        {
            using (Init())
            {
                var evts = new KeyValuePair<Guid, object>[5];
                for (int i = 0; i < 5; i++)
                    evts[i] = new KeyValuePair<Guid, object>(Guid.NewGuid(), (expected.Count + i).ToString());

                var result1 = await stream.Append(evts, 0);
                var result2 = await stream.Append(evts, 0);

                Assert.Equal(true, result1);
                Assert.Equal(true, result2);
            }
        }
       
        private async Task CheckBunchOfQueries(int max = 2)
        {
            // check version
            var version = await stream.GetVersion();
            Assert.Equal(expected.Count, version);

            // check contained subranges
            await CompareToExpected(0);
            await CompareToExpected(0, 0);
            await CompareToExpected(0, 1);
            await CompareToExpected(0, max);
            await CompareToExpected(1);
            await CompareToExpected(1, 1);
            await CompareToExpected(max);
            await CompareToExpected(max - 2, max);
            await CompareToExpected(1, max);
            await CompareToExpected(max - 1, max);
            await CompareToExpected(max, max);
            await CompareToExpected(max, max + 1);

            // check subranges that may not be contained
            await CompareToExpected(0, max + 1);
            await CompareToExpected(0, max + 600);
            await CompareToExpected(max + 1);
            await CompareToExpected(max + 1, max + 1);
            await CompareToExpected(max + 100, max + 200);

            // check invalid subranges
            await CompareToExpected(1, 0);
            await CompareToExpected(-1);
            await CompareToExpected(-1, 0);
            await CompareToExpected(-1, max);
            await CompareToExpected(max + 1, max);
            await CompareToExpected(max, 0);
        }


        private async Task Append(int howMany, int? expVersion = null)
        {
            var evts = new List<KeyValuePair<Guid, string>>();
            for (int i = 0; i < howMany; i++)
                evts.Add(new KeyValuePair<Guid, string>(Guid.NewGuid(), (expected.Count + i).ToString()));

            var response = await stream.Append(
                evts.Select(kvp => new KeyValuePair<Guid, object>(kvp.Key, kvp.Value)), expVersion);

            if (!expVersion.HasValue || expVersion == expected.Count)
            {
                // append must succeed
                Assert.Equal(true, response);
                expected.AddRange(evts);
            }
            else
            {
                // append must fail
                Assert.Equal(false, response);
            }
        }

        private async Task Delete(int? expVersion = null)
        {
            var response = await stream.Delete(expVersion);

            if (!expVersion.HasValue || expVersion == expected.Count)
            {
                // append must succeed
                Assert.Equal(true, response);
                expected.Clear();
            }
            else
            {
                // append must fail
                Assert.Equal(false, response);
            }
        }

        private async Task CompareToExpected(int start, int? end = null)
        {
            if (start < 0 || end < start)
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                   stream.Load<string>(start, end));
            }
            else
            {
                var response = await stream.Load<string>(start, end);

                Assert.Equal(response.StreamName, stream.StreamName);
                Assert.NotNull(response.Events);
                Assert.Equal(response.FromVersion + response.Events.Count, response.ToVersion);

                // the returned segment must be a valid segment, with correct content
                Assert.True(0 <= response.FromVersion);
                Assert.True(response.FromVersion <= expected.Count);
                Assert.True(response.ToVersion <= expected.Count);
                for (int i = 0; i < response.Events.Count; i++)
                {
                    Assert.Equal(expected[response.FromVersion + i].Key, response.Events[i].Key);
                    Assert.Equal(expected[response.FromVersion + i].Value, response.Events[i].Value);
                }

                // the returned segment must start at the requested version, if valid
                if (0 <= start && start <= expected.Count)
                {
                    Assert.Equal(start, response.FromVersion);

                    if (end.HasValue)
                    {
                        // returned segment must have the requested length, if in bounds
                        if (start <= end && end <= expected.Count)
                            Assert.Equal(end - start, response.Events.Count);
                    }
                    else
                    {
                        // returned segment must have max length
                        Assert.Equal(expected.Count - response.FromVersion, response.Events.Count);
                    }
                }


            }
        }
    }
}
