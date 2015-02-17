using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using NewReminderLoadTest;

namespace StreamPullingAgentBenchmark.NewReminderLoadTest
{
    public class ReminderRegisterer
    {
        public struct ReportedValues
        {
            public long ReminderSuccessCount { get; set; }

            public long ReminderAttemptCount { get; set; }

            public double AverageReminderLatency { get; set; }
        }

        private readonly object _tickLock = new object();

        private readonly object _valueLock = new object();

        private readonly NewReminderOptions _options;

        private readonly HashSet<Task> _registerTasks = new HashSet<Task>();

        private Timer _timer;

        private DateTime _startTime;

        private long _count;

        private Value _completedCount;

        private Value _sucessCount;

        private Value _latency;

        public ReminderRegisterer(NewReminderOptions options)
        {
            this._options = options;
        }

        public void StartRegistering()
        {
            _registerTasks.Clear();

            var period = TimeSpan.FromSeconds(1.0 / _options.RemindersPerSecond);
            _timer = new Timer(this.OnTick, null, period, period);

            _startTime = DateTime.UtcNow;
            _count = 0;
            this._completedCount = new Value();
            this._sucessCount = new Value();
            this._latency = new Value();
        }

        public void StopRegistering()
        {
            _timer.Dispose();
        }

        public void OnTick(object state)
        {
            bool lockTaken = false;
            Monitor.Enter(_tickLock, ref lockTaken);

            if (!lockTaken)
            {
                return;
            }

            try
            {
                long targetTotalRegisterCount = (long)((DateTime.UtcNow - _startTime).TotalSeconds * _options.RemindersPerSecond);
                long registerCountDefecit = Math.Max(0, targetTotalRegisterCount - _count);

                if (registerCountDefecit == 0)
                {
                    return;
                }

                _registerTasks.RemoveWhere(t => t.IsCompleted);
                int availableRegisterTaskCount = Math.Max(0, _options.ConcurrentRequests - _registerTasks.Count);
                long registerCountThisTick = Math.Min(registerCountDefecit, availableRegisterTaskCount);
                _count += registerCountThisTick;
                for (long i = 0; i < registerCountThisTick; i++)
                {
                    _registerTasks.Add(RegisterReminderAsync());
                }

            }
            finally
            {
                Monitor.Exit(_tickLock);
            }
        }

        private async Task RegisterReminderAsync()
        {
            var reminderGrain = ReminderGrainFactory.GetGrain(Guid.NewGuid());
            var stopwatch = Stopwatch.StartNew();

            bool success = false;
            try
            {
                success = await reminderGrain.RegisterReminder(
                    "reminder",
                    TimeSpan.FromSeconds(_options.ReminderPeriod),
                    TimeSpan.FromSeconds(_options.ReminderDuration),
                    _options.SkipGet);
            }
            catch (Exception ex)
            {
                Utilities.LogIfVerbose(ex + "\n" + ex.StackTrace, _options);
            }

            stopwatch.Stop();

            lock (_valueLock)
            {
                _completedCount.Increment(1);
                if (success)
                {
                    _sucessCount.Increment(1);
                }
                _latency.Increment(stopwatch.ElapsedMilliseconds);
            }
        }

        public ReportedValues Flush()
        {
            lock (_valueLock)
            {
                var values = new ReportedValues
                {
                    ReminderSuccessCount = _sucessCount.Delta,
                    ReminderAttemptCount = _completedCount.Delta,
                    AverageReminderLatency = _latency.DeltaAverage
                };
                FlushValues();
                return values;
            }
        }

        public ReportedValues Total()
        {
            lock (_valueLock)
            {
                var values = new ReportedValues
                {
                    ReminderSuccessCount = _sucessCount.Total,
                    ReminderAttemptCount = _completedCount.Total,
                    AverageReminderLatency = _latency.TotalAverage
                };
                return values;
            }
        }

        private void FlushValues()
        {
            _sucessCount.Flush();
            _completedCount.Flush();
            _latency.Flush();
        }
    }
}