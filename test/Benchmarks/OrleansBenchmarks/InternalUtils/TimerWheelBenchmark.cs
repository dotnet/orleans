using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Orleans.Runtime;
using SerializationBenchmarks;

namespace OrleansBenchmarks
{
    [Config(typeof(InternalUtilsBenchmarkConfig))]
    public class TimerWheelBenchmark
    {
        [Params(3000000, 10000000)]
        public int Repeats { get; set; }
        private readonly TimerCallback callback = new TimerCallback(state => { });

        [ThreadStatic]
        private static TimerWheel<TimeboundPoco> timerWheel;

        private readonly TimeSpan wheelCheckPeriod = TimeSpan.FromMilliseconds(50);
        private CountdownEvent countdownEvent;

        [Benchmark]
        public void TimerWheelTimeout()
        {
            countdownEvent = new CountdownEvent(Repeats);
            timerWheel = new TimerWheel<TimeboundPoco>(wheelCheckPeriod, poco => false, poco => false);
            TimerWheelBenchmarkCore();
        }

        [Benchmark]
        public void TimerWheelCommonCase()
        {
            countdownEvent = new CountdownEvent(Repeats);
            timerWheel = new TimerWheel<TimeboundPoco>(wheelCheckPeriod, poco => false, poco =>
            {
                countdownEvent.Signal();
                return true;
            });
            TimerWheelBenchmarkCore();
        }

        [Benchmark]
        public void DotNetTimer()
        {
            for (int i = 0; i < Repeats; i++)
            {
                Timer timer = new Timer(callback, null, wheelCheckPeriod, wheelCheckPeriod);
                timer.Dispose();
            }
        }

        [Benchmark]
        public void ParallelDotNetTimer()
        {
            Parallel.For(0, Repeats, (state) =>
            {
                Timer timer = new Timer(callback, null, wheelCheckPeriod, wheelCheckPeriod);
                timer.Dispose();
            });
        }

        private void TimerWheelBenchmarkCore()
        {
            var timeboundPoco = new TimeboundPoco(DateTime.UtcNow, countdownEvent);
            for (int i = 0; i < Repeats; i++)
            {
                timerWheel.Register(timeboundPoco);
            }

            countdownEvent.Wait();
        }
    }

    internal class TimeboundPoco : ITimebound
    {
        private CountdownEvent _countdownEvent;
        public TimeboundPoco(DateTime dueTime, CountdownEvent countdownEvent) //
        {
            DueTime = dueTime;
            _countdownEvent = countdownEvent;
        }

        public void OnTimeout()
        {
            _countdownEvent.Signal();
        }

        public DateTime DueTime { get; }
    }
}
