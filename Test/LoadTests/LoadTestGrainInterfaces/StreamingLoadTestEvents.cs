using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace LoadTestGrainInterfaces
{
    [Serializable]
    [Immutable]
    public abstract class StreamingLoadTestBaseEvent
    {
        public int TaskDelayMs { get; set; }

        public int BusyWaitMs { get; set; }

        public byte[] Data { get; set; }

        public async Task TaskDelay()
        {
            if (TaskDelayMs > 0)
            {
                await Task.Delay(TaskDelayMs);
            }
        }

        public void BusyWait()
        {
            if (BusyWaitMs > 0)
            {
                Stopwatch watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < BusyWaitMs)
                {
                    // Do nothing - Busy loop.
                }
            }
        }
    }

    [Serializable]
    [Immutable]
    public class StreamingLoadTestStartEvent : StreamingLoadTestBaseEvent
    {
        public string StreamProvider { get; set; }
        public int AdditionalSubscribersCount { get; set; }
    }

    [Serializable]
    [Immutable]
    public class StreamingLoadTestEvent : StreamingLoadTestBaseEvent
    {
    }

    [Serializable]
    [Immutable]
    public class StreamingLoadTestEndEvent : StreamingLoadTestBaseEvent
    {
    }
}