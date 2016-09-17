using System;
using System.Threading;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.General
{
    public class TimerWheelTest
    {
        private readonly TimeSpan _defaultCheckPeriod = TimeSpan.FromMilliseconds(15);
        private const int Repeats = 15;

        [Fact, TestCategory("Functional"), TestCategory("TimerWheel")]
        public void TimeWheel_BasicTimeout()
        {
            TimerWheel<TimeboundPoco> timerWheel = new TimerWheel<TimeboundPoco>(_defaultCheckPeriod);
            CountdownEvent countdownEvent = new CountdownEvent(Repeats);
            VerifyCore(timerWheel, countdownEvent, Repeats,
                () => new TimeboundPoco(DateTime.UtcNow, () => countdownEvent.Signal()));
            Assert.Equal(0, timerWheel.QueueCount);
        }

        [Fact, TestCategory("Functional"), TestCategory("TimerWheel")]
        public void TimeWheel_DequeueFunc()
        {
            CountdownEvent countdownEvent = new CountdownEvent(Repeats);
            TimerWheel<TimeboundPoco> timerWheel = new TimerWheel<TimeboundPoco>(_defaultCheckPeriod, shouldDequeue: (poco
                =>
            {
                countdownEvent.Signal();
                return true;
            }));

            VerifyCore(timerWheel, countdownEvent, Repeats,
                () => new TimeboundPoco(DateTime.UtcNow, () =>
                {
                    throw new Exception("Shoud not call on timeout when dequeued through provided user function");
                }));

            Assert.Equal(0, timerWheel.QueueCount);
        }

        [Fact, TestCategory("Functional"), TestCategory("TimerWheel")]
        public void TimeWheel_ReEnqueueFunc()
        {
            TimerWheel<TimeboundPoco> timerWheel = new TimerWheel<TimeboundPoco>(_defaultCheckPeriod, poco =>
            {
                poco.DueTime = DateTime.UtcNow.AddDays(1);
                return true;
            });

            CountdownEvent countdownEvent = new CountdownEvent(Repeats);
            VerifyCore(timerWheel, countdownEvent, Repeats,
                () => new TimeboundPoco(DateTime.UtcNow, () => countdownEvent.Signal()));
            Assert.Equal(Repeats, timerWheel.QueueCount);
        }

        private void VerifyCore(TimerWheel<TimeboundPoco> wheel, CountdownEvent countdownEvent, int repeats, Func<TimeboundPoco> entryFact)
        {
            for (int i = 0; i < repeats - 1; i++)
            {
                wheel.Register(entryFact());
            }

            Thread.Sleep(25);
            wheel.Register(entryFact());

            try
            {
                countdownEvent.Wait(TimeSpan.FromMilliseconds(300));
            }
            catch
            {
                Assert.False(true, "All registered entries should've been timeouted");
                throw;
            }
        }
    }

    internal class TimeboundPoco : ITimebound
    {
        private readonly Action _onTimeout;
        public TimeboundPoco(DateTime dueTime, Action onTimeout)
        {
            DueTime = dueTime;
            if(onTimeout == null) throw new ArgumentNullException(nameof(onTimeout));
            _onTimeout = onTimeout;
        }

        public void OnTimeout()
        {
            _onTimeout();
        }

        public DateTime DueTime { get; set; }
    }
}
