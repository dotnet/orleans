using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;


namespace UnitTestGrains
{
    public class TimerGrain : Grain, ITimerGrain
    {
        private bool deactivating;
        private int counter = 0;
        private Dictionary<string, IDisposable> allTimers;
        private IDisposable defaultTimer;
        private static readonly TimeSpan period = TimeSpan.FromMilliseconds(100);
        private readonly string DefaultTimerName = "DEFAULT TIMER";
        private IGrainContext context;

        private readonly ILogger logger;

        public TimerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ThrowIfDeactivating();
            context = RuntimeContext.Current;
            defaultTimer = this.RegisterGrainTimer(Tick, DefaultTimerName, period, period);
            allTimers = new Dictionary<string, IDisposable>();
            return Task.CompletedTask;
        }

        public Task StopDefaultTimer()
        {
            ThrowIfDeactivating();
            defaultTimer.Dispose();
            return Task.CompletedTask;
        }
        private Task Tick(object data)
        {
            counter++;
            logger.LogInformation(
                "{Data} Tick # {Counter} RuntimeContext = {RuntimeContext}",
                data,
                counter,
                RuntimeContext.Current);

            // make sure we run in the right activation context.
            if (!Equals(context, RuntimeContext.Current))
                logger.LogError((int)ErrorCode.Runtime_Error_100146, "Grain not running in the right activation context");

            string name = (string)data;
            IDisposable timer;
            if (name == DefaultTimerName)
            {
                timer = defaultTimer;
            }
            else
            {
                timer = allTimers[(string)data];
            }
            if (timer == null)
                logger.LogError((int)ErrorCode.Runtime_Error_100146, "Timer is null");
            if (timer != null && counter > 10000)
            {
                // do not let orphan timers ticking for long periods
                timer.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task<TimeSpan> GetTimerPeriod()
        {
            return Task.FromResult(period);
        }

        public Task<int> GetCounter()
        {
            ThrowIfDeactivating();
            return Task.FromResult(counter);
        }
        public Task SetCounter(int value)
        {
            ThrowIfDeactivating();
            lock (this)
            {
                counter = value;
            }
            return Task.CompletedTask;
        }
        public Task StartTimer(string timerName)
        {
            ThrowIfDeactivating();
            IDisposable timer = this.RegisterGrainTimer(Tick, timerName, TimeSpan.Zero, period);
            allTimers.Add(timerName, timer);
            return Task.CompletedTask;
        }

        public Task StopTimer(string timerName)
        {
            ThrowIfDeactivating();
            IDisposable timer = allTimers[timerName];
            timer.Dispose();
            return Task.CompletedTask;
        }

        public Task LongWait(TimeSpan time)
        {
            ThrowIfDeactivating();
            Thread.Sleep(time);
            return Task.CompletedTask;
        }

        public Task Deactivate()
        {
            deactivating = true;
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        private void ThrowIfDeactivating()
        {
            if (deactivating) throw new InvalidOperationException("This activation is deactivating");
        }
    }

    public class TimerCallGrain : Grain, ITimerCallGrain
    {
        private int tickCount;
        private Exception tickException;
        private IGrainTimer timer;
        private string timerName;
        private IGrainContext context;
        private TaskScheduler activationTaskScheduler;

        private readonly ILogger logger;

        public TimerCallGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public Task<int> GetTickCount() { return Task.FromResult(tickCount); }
        public Task<Exception> GetException() { return Task.FromResult(tickException); }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            context = RuntimeContext.Current;
            activationTaskScheduler = TaskScheduler.Current;
            return Task.CompletedTask;
        }

        public Task StartTimer(string name, TimeSpan delay)
        {
            logger.LogInformation("StartTimer Name={Name} Delay={Delay}", name, delay);
            if (timer is not null) throw new InvalidOperationException("Expected timer to be null");
            this.timer = this.RegisterGrainTimer(TimerTick, name, new(delay, Timeout.InfiniteTimeSpan) { Interleave = true }); // One shot timer
            this.timerName = name;

            return Task.CompletedTask;
        }

        public Task StartTimer(string name, TimeSpan delay, string operationType)
        {
            logger.LogInformation("StartTimer Name={Name} Delay={Delay}", name, delay);
            if (timer is not null) throw new InvalidOperationException("Expected timer to be null");
            var state = Tuple.Create<string, object>(operationType, name);
            this.timer = this.RegisterGrainTimer(TimerTickAdvanced, state, new(delay, Timeout.InfiniteTimeSpan) { Interleave = true }); // One shot timer
            this.timerName = name;

            return Task.CompletedTask;
        }

        public Task RestartTimer(string name, TimeSpan delay)
        {
            logger.LogInformation("RestartTimer Name={Name} Delay={Delay}", name, delay);
            this.timerName = name;
            timer.Change(delay, Timeout.InfiniteTimeSpan);

            return Task.CompletedTask;
        }

        public Task RestartTimer(string name, TimeSpan delay, TimeSpan period)
        {
            logger.LogInformation("RestartTimer Name={Name} Delay={Delay} Period={Period}", name, delay, period);
            this.timerName = name;
            timer.Change(delay, period);

            return Task.CompletedTask;
        }

        public Task StopTimer(string name)
        {
            logger.LogInformation("StopTimer Name={Name}", name);
            if (name != this.timerName)
            {
                throw new ArgumentException($"Wrong timer name: Expected={this.timerName} Actual={name}");
            }

            timer.Dispose();
            timer = null;
            timerName = null;
            return Task.CompletedTask;
        }

        public async Task RunSelfDisposingTimer()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new IGrainTimer[1];
            timer[0] = this.RegisterGrainTimer(async (ct) =>
            {
                try
                {
                    Assert.False(ct.IsCancellationRequested);
                    Assert.NotNull(timer[0]);
                    timer[0].Dispose();
                    Assert.True(ct.IsCancellationRequested);
                    await Task.Delay(100);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            new GrainTimerCreationOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            {
                Interleave = true
            });

            await tcs.Task;
        }

        private async Task TimerTick(object data)
        {
            try
            {
                await ProcessTimerTick(data);
            }
            catch (Exception exc)
            {
                this.tickException = exc;
                throw;
            }
        }

        private async Task TimerTickAdvanced(object data)
        {
            try
            {
                var state = (Tuple<string, object>)data;
                var operation = state.Item1;
                var name = state.Item2;

                await ProcessTimerTick(name);

                if (operation == "update_period")
                {
                    var newPeriod = TimeSpan.FromSeconds(100);
                    timer.Change(newPeriod, newPeriod);
                }
                else if (operation == "dispose_timer")
                {
                    await StopTimer((string)name);
                }
            }
            catch (Exception exc)
            {
                this.tickException = exc;
                throw;
            }
        }

        private async Task ProcessTimerTick(object data)
        {
            string step = "TimerTick";
            LogStatus(step);
            // make sure we run in the right activation context.
            CheckRuntimeContext(step);

            string name = (string)data;
            if (name != this.timerName)
            {
                throw new ArgumentException(string.Format("Wrong timer name: Expected={0} Actual={1}", this.timerName, name));
            }

            ISimpleGrain grain = GrainFactory.GetGrain<ISimpleGrain>(0, SimpleGrain.SimpleGrainNamePrefix);

            LogStatus("Before grain call #1");
            await grain.SetA(tickCount);
            step = "After grain call #1";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before Delay");
            await Task.Delay(TimeSpan.FromSeconds(1));
            step = "After Delay";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before grain call #2");
            await grain.SetB(tickCount);
            step = "After grain call #2";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before grain call #3");
            int res = await grain.GetAxB();
            step = "After grain call #3 - Result = " + res;
            LogStatus(step);
            CheckRuntimeContext(step);

            tickCount++;
        }

        private void CheckRuntimeContext(string what)
        {
            if (RuntimeContext.Current == null
                || !RuntimeContext.Current.Equals(context))
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected activation context: Expected={1} Actual={2}",
                                  what, context, RuntimeContext.Current));
            }
            if (TaskScheduler.Current.Equals(activationTaskScheduler) && TaskScheduler.Current is ActivationTaskScheduler)
            {
                // Everything is as expected
            }
            else
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected TaskScheduler.Current context: Expected={1} Actual={2}",
                                  what, activationTaskScheduler, TaskScheduler.Current));
            }
        }

        private void LogStatus(string what)
        {
            logger.LogInformation(
                "{TimerName} Tick # {TickCount} - {Step} - RuntimeContext.Current={RuntimeContext} TaskScheduler.Current={TaskScheduler} CurrentWorkerThread={Thread}",
                timerName,
                tickCount,
                what,
                RuntimeContext.Current,
                TaskScheduler.Current,
                Thread.CurrentThread.Name);
        }
    }

    public class NonReentrantTimerCallGrain : Grain, INonReentrantTimerCallGrain
    {
        private readonly Dictionary<string, IGrainTimer> _timers = [];
        private int _tickCount;
        private Exception _tickException;
        private IGrainContext _context;
        private TaskScheduler _activationTaskScheduler;
        private Guid _tickId;

        private readonly ILogger _logger;

        public NonReentrantTimerCallGrain(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger($"{GetType().Name}-{IdentityString}");
        }

        public Task<int> GetTickCount() => Task.FromResult(_tickCount);
        public Task<Exception> GetException() => Task.FromResult(_tickException);

        public async Task ExternalTick(string name)
        {
            await ProcessTimerTick(name, CancellationToken.None);
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _context = RuntimeContext.Current;
            _activationTaskScheduler = TaskScheduler.Current;
            return Task.CompletedTask;
        }

        public Task StartTimer(string name, TimeSpan delay, bool keepAlive)
        {
            _logger.LogInformation("StartTimer Name={Name} Delay={Delay}", name, delay);
            if (_timers.TryGetValue(name, out var timer))
            {
                // Make the timer fire again after the specified delay.
                timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timers[name] = this.RegisterGrainTimer(TimerTick, name, new() { DueTime = delay, Period = Timeout.InfiniteTimeSpan, KeepAlive = keepAlive }); // One shot timer
            }

            return Task.CompletedTask;
        }

        public Task StopTimer(string name)
        {
            _logger.LogInformation("StopTimer Name={Name}", name);

            if (!_timers.Remove(name, out var timer))
            {
                throw new ArgumentException($"Could not find a timer with name {name}.");
            }

            timer.Dispose();
            return Task.CompletedTask;
        }

        private async Task TimerTick(object data, CancellationToken cancellationToken)
        {
            try
            {
                await ProcessTimerTick(data, cancellationToken);
            }
            catch (Exception exc)
            {
                _tickException = exc;
                throw;
            }
        }

        private async Task ProcessTimerTick(object data, CancellationToken cancellationToken)
        {
            var timerName = (string)data;
            string step = "TimerTick";
            CheckReentrancy(step, Guid.Empty);
            var expectedTickId = _tickId = Guid.NewGuid();
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            ISimpleGrain grain = GrainFactory.GetGrain<ISimpleGrain>(0, SimpleGrain.SimpleGrainNamePrefix);

            LogStatus("Before grain call #1", timerName);
            await grain.SetA(_tickCount);
            step = "After grain call #1";
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            LogStatus("Before Delay", timerName);
            await Task.Delay(TimeSpan.FromSeconds(1));
            step = "After Delay";
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            LogStatus("Before grain call #2", timerName);
            await grain.SetB(_tickCount);
            step = "After grain call #2";
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            LogStatus("Before grain call #3", timerName);
            int res = await grain.GetAxB();
            step = "After grain call #3 - Result = " + res;
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            _tickCount++;
            _tickId = Guid.Empty;
        }

        private void CheckRuntimeContext(string what)
        {
            if (RuntimeContext.Current == null
                || !RuntimeContext.Current.Equals(_context))
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected activation context: Expected={1} Actual={2}",
                                  what, _context, RuntimeContext.Current));
            }
            if (TaskScheduler.Current.Equals(_activationTaskScheduler) && TaskScheduler.Current is ActivationTaskScheduler)
            {
                // Everything is as expected
            }
            else
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected TaskScheduler.Current context: Expected={1} Actual={2}",
                                  what, _activationTaskScheduler, TaskScheduler.Current));
            }
        }

        private void CheckReentrancy(string what, Guid expected)
        {
            if (_tickId != expected)
            {
                throw new InvalidOperationException(
                    $"{what} in timer callback with unexpected interleaving: Expected={expected} Actual={_tickId}");
            }
        }

        private void LogStatus(string what, string timerName)
        {
            _logger.LogInformation(
                "{TimerName} Tick # {TickCount} - {Step} - RuntimeContext.Current={RuntimeContext} TaskScheduler.Current={TaskScheduler} CurrentWorkerThread={Thread}",
                timerName,
                _tickCount,
                what,
                RuntimeContext.Current,
                TaskScheduler.Current,
                Thread.CurrentThread.Name);
        }
    }

    public class TimerRequestGrain : Grain, ITimerRequestGrain
    {
        private TaskCompletionSource<int> completionSource;
        private List<TaskCompletionSource<(object, CancellationToken)>> _allTimerCallsTasks;

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public async Task StartAndWaitTimerTick(TimeSpan dueTime)
        {
            this.completionSource = new TaskCompletionSource<int>();
            using var timer = this.RegisterGrainTimer(TimerTick, new() { DueTime = dueTime, Period = Timeout.InfiniteTimeSpan, Interleave = true });
            await this.completionSource.Task;
        }

        public Task StartStuckTimer(TimeSpan dueTime)
        {
            this.completionSource = new TaskCompletionSource<int>();
            var timer = this.RegisterGrainTimer(StuckTimerTick, new() { DueTime = dueTime, Period = TimeSpan.FromSeconds(1), Interleave = true });
            return Task.CompletedTask;
        }

        private Task TimerTick()
        {
            this.completionSource.TrySetResult(1);
            return Task.CompletedTask;
        }

        private async Task StuckTimerTick(CancellationToken cancellationToken)
        {
            await completionSource.Task;
        }

        public Task<int> TestAllTimerOverloads()
        {
            var tasks = new List<TaskCompletionSource<(object, CancellationToken)>>();
            var timers = new List<IGrainTimer>();

            // protected IGrainTimer RegisterGrainTimer(Func<Task> callback, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(() =>
            {
                tasks[0].TrySetResult(("NONE", CancellationToken.None));
                return Task.CompletedTask;
            }, new GrainTimerCreationOptions(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)) { Interleave = true }));

            // protected IGrainTimer RegisterGrainTimer(Func<Task> callback, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(() =>
            {
                tasks[1].TrySetResult(("NONE", CancellationToken.None));
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)));

            // protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, Task> callback, TState state, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(state =>
            {
                tasks[2].TrySetResult((state, CancellationToken.None));
                return Task.CompletedTask;
            },
            "STATE",
            new GrainTimerCreationOptions(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)) { Interleave = true }));

            // protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(state =>
            {
                tasks[3].TrySetResult((state, CancellationToken.None));
                return Task.CompletedTask;
            },
            "STATE",
            TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)));

            // With CancellationToken
            // protected IGrainTimer RegisterGrainTimer(Func<CancellationToken, Task> callback, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(ct =>
            {
                tasks[4].TrySetResult(("NONE", ct));
                return Task.CompletedTask;
            }, new GrainTimerCreationOptions(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)) { Interleave = true }));

            // protected IGrainTimer RegisterGrainTimer(Func<CancellationToken, Task> callback, TimeSpan dueTime, TimeSpan period)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(ct =>
            {
                tasks[5].TrySetResult(("NONE", ct));
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)));

            // protected internal IGrainTimer RegisterGrainTimer<TState>(Func<TState, CancellationToken, Task> callback, TState state, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer((state, ct) =>
            {
                tasks[6].TrySetResult((state, ct));
                return Task.CompletedTask;
            },
            "STATE",
            new GrainTimerCreationOptions(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)) { Interleave = true }));

            // protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, CancellationToken, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer((state, ct) =>
            {
                tasks[7].TrySetResult((state, ct));
                return Task.CompletedTask;
            },
            "STATE",
            TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)));
            _allTimerCallsTasks = tasks;
            return Task.FromResult(_allTimerCallsTasks.Count);
        }

        public Task<int> PollCompletedTimers() => Task.FromResult(_allTimerCallsTasks.Count(c => c.Task.IsCompleted));
        public async Task TestCompletedTimerResults()
        {
            var countWithState = 0;
            var countWithCancellation = 0;

            foreach (var task in _allTimerCallsTasks.Select(t => t.Task))
            {
                var (state, ct) = await task;
                var stateString  = Assert.IsType<string>(state);
                var hasState = string.Equals("STATE", stateString, StringComparison.Ordinal);
                if (hasState)
                {
                    countWithState++;
                }

                Assert.True(hasState || string.Equals("NONE", stateString, StringComparison.Ordinal));
                if (ct.CanBeCanceled)
                {
                    countWithCancellation++;
                }
            }

            Assert.Equal(4, countWithState);
            Assert.Equal(4, countWithCancellation);
        }
    }

    public class PocoTimerGrain : IGrainBase, IPocoTimerGrain
    {
        private bool deactivating;
        private int counter = 0;
        private Dictionary<string, IDisposable> allTimers;
        private IDisposable defaultTimer;
        private static readonly TimeSpan period = TimeSpan.FromMilliseconds(100);
        private readonly string DefaultTimerName = "DEFAULT TIMER";
        private IGrainContext context;

        private readonly ILogger logger;

        public IGrainContext GrainContext { get; }

        public PocoTimerGrain(ILoggerFactory loggerFactory, IGrainContext context)
        {
            GrainContext = context;
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{context.GrainId}");
        }

        public Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ThrowIfDeactivating();
            context = RuntimeContext.Current;
            defaultTimer = this.RegisterGrainTimer(Tick, DefaultTimerName, period, period);
            allTimers = new Dictionary<string, IDisposable>();
            return Task.CompletedTask;
        }

        public Task StopDefaultTimer()
        {
            ThrowIfDeactivating();
            defaultTimer.Dispose();
            return Task.CompletedTask;
        }

        private Task Tick(object data)
        {
            counter++;
            logger.LogInformation(
                "{Data} Tick # {Counter} RuntimeContext = {RuntimeContext}",
                data,
                counter,
                RuntimeContext.Current);

            // make sure we run in the right activation context.
            if (!Equals(context, RuntimeContext.Current))
                logger.LogError((int)ErrorCode.Runtime_Error_100146, "Grain not running in the right activation context");

            string name = (string)data;
            IDisposable timer;
            if (name == DefaultTimerName)
            {
                timer = defaultTimer;
            }
            else
            {
                timer = allTimers[(string)data];
            }
            if (timer == null)
                logger.LogError((int)ErrorCode.Runtime_Error_100146, "Timer is null");
            if (timer != null && counter > 10000)
            {
                // do not let orphan timers ticking for long periods
                timer.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task<TimeSpan> GetTimerPeriod()
        {
            return Task.FromResult(period);
        }

        public Task<int> GetCounter()
        {
            ThrowIfDeactivating();
            return Task.FromResult(counter);
        }
        public Task SetCounter(int value)
        {
            ThrowIfDeactivating();
            lock (this)
            {
                counter = value;
            }
            return Task.CompletedTask;
        }
        public Task StartTimer(string timerName)
        {
            ThrowIfDeactivating();
            IDisposable timer = this.RegisterGrainTimer(Tick, timerName, TimeSpan.Zero, period);
            allTimers.Add(timerName, timer);
            return Task.CompletedTask;
        }

        public Task StopTimer(string timerName)
        {
            ThrowIfDeactivating();
            IDisposable timer = allTimers[timerName];
            timer.Dispose();
            return Task.CompletedTask;
        }

        public Task LongWait(TimeSpan time)
        {
            ThrowIfDeactivating();
            Thread.Sleep(time);
            return Task.CompletedTask;
        }

        public Task Deactivate()
        {
            deactivating = true;
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        private void ThrowIfDeactivating()
        {
            if (deactivating) throw new InvalidOperationException("This activation is deactivating");
        }
    }

    public class PocoTimerCallGrain : IGrainBase, IPocoTimerCallGrain
    {
        private int tickCount;
        private Exception tickException;
        private IGrainTimer timer;
        private string timerName;
        private IGrainContext context;
        private TaskScheduler activationTaskScheduler;

        private readonly ILogger logger;
        private readonly IGrainFactory _grainFactory;

        public IGrainContext GrainContext { get; }

        public PocoTimerCallGrain(ILoggerFactory loggerFactory, IGrainContext grainContext, IGrainFactory grainFactory)
        {
            GrainContext = grainContext;
            _grainFactory = grainFactory;
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.GrainContext.GrainId}");
        }

        public Task<int> GetTickCount() { return Task.FromResult(tickCount); }
        public Task<Exception> GetException() { return Task.FromResult(tickException); }

        public Task OnActivateAsync(CancellationToken cancellationToken)
        {
            context = RuntimeContext.Current;
            activationTaskScheduler = TaskScheduler.Current;
            return Task.CompletedTask;
        }

        public Task StartTimer(string name, TimeSpan delay)
        {
            logger.LogInformation("StartTimer Name={Name} Delay={Delay}", name, delay);
            if (timer is not null) throw new InvalidOperationException("Expected timer to be null");
            this.timer = this.RegisterGrainTimer(TimerTick, name, new(delay, Timeout.InfiniteTimeSpan)); // One shot timer
            this.timerName = name;

            return Task.CompletedTask;
        }

        public Task StartTimer(string name, TimeSpan delay, string operationType)
        {
            logger.LogInformation("StartTimer Name={Name} Delay={Delay}", name, delay);
            if (timer is not null) throw new InvalidOperationException("Expected timer to be null");
            var state = Tuple.Create<string, object>(operationType, name);
            this.timer = this.RegisterGrainTimer(TimerTickAdvanced, state, delay, Timeout.InfiniteTimeSpan); // One shot timer
            this.timerName = name;

            return Task.CompletedTask;
        }

        public Task RestartTimer(string name, TimeSpan delay)
        {
            logger.LogInformation("RestartTimer Name={Name} Delay={Delay}", name, delay);
            this.timerName = name;
            timer.Change(delay, Timeout.InfiniteTimeSpan);

            return Task.CompletedTask;
        }

        public Task RestartTimer(string name, TimeSpan delay, TimeSpan period)
        {
            logger.LogInformation("RestartTimer Name={Name} Delay={Delay} Period={Period}", name, delay, period);
            this.timerName = name;
            timer.Change(delay, period);

            return Task.CompletedTask;
        }

        public Task StopTimer(string name)
        {
            logger.LogInformation("StopTimer Name={Name}", name);
            if (name != this.timerName)
            {
                throw new ArgumentException($"Wrong timer name: Expected={this.timerName} Actual={name}");
            }

            timer.Dispose();
            timer = null;
            timerName = null;
            return Task.CompletedTask;
        }

        public async Task RunSelfDisposingTimer()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new IGrainTimer[1];
            timer[0] = this.RegisterGrainTimer(async (ct) =>
            {
                try
                {
                    Assert.False(ct.IsCancellationRequested);
                    Assert.NotNull(timer[0]);
                    timer[0].Dispose();
                    Assert.True(ct.IsCancellationRequested);
                    await Task.Delay(100);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            new GrainTimerCreationOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            {
                Interleave = true
            });

            await tcs.Task;
        }

        private async Task TimerTick(object data)
        {
            try
            {
                await ProcessTimerTick(data);
            }
            catch (Exception exc)
            {
                this.tickException = exc;
                throw;
            }
        }

        private async Task TimerTickAdvanced(object data)
        {
            try
            {
                var state = (Tuple<string, object>)data;
                var operation = state.Item1;
                var name = state.Item2;

                await ProcessTimerTick(name);

                if (operation == "update_period")
                {
                    var newPeriod = TimeSpan.FromSeconds(100);
                    timer.Change(newPeriod, newPeriod);
                }
                else if (operation == "dispose_timer")
                {
                    await StopTimer((string)name);
                }
            }
            catch (Exception exc)
            {
                this.tickException = exc;
                throw;
            }
        }

        private async Task ProcessTimerTick(object data)
        {
            string step = "TimerTick";
            LogStatus(step);
            // make sure we run in the right activation context.
            CheckRuntimeContext(step);

            string name = (string)data;
            if (name != this.timerName)
            {
                throw new ArgumentException(string.Format("Wrong timer name: Expected={0} Actual={1}", this.timerName, name));
            }

            ISimpleGrain grain = _grainFactory.GetGrain<ISimpleGrain>(0, SimpleGrain.SimpleGrainNamePrefix);

            LogStatus("Before grain call #1");
            await grain.SetA(tickCount);
            step = "After grain call #1";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before Delay");
            await Task.Delay(TimeSpan.FromSeconds(1));
            step = "After Delay";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before grain call #2");
            await grain.SetB(tickCount);
            step = "After grain call #2";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before grain call #3");
            int res = await grain.GetAxB();
            step = "After grain call #3 - Result = " + res;
            LogStatus(step);
            CheckRuntimeContext(step);

            tickCount++;
        }

        private void CheckRuntimeContext(string what)
        {
            if (RuntimeContext.Current == null
                || !RuntimeContext.Current.Equals(context))
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected activation context: Expected={1} Actual={2}",
                                  what, context, RuntimeContext.Current));
            }
            if (TaskScheduler.Current.Equals(activationTaskScheduler) && TaskScheduler.Current is ActivationTaskScheduler)
            {
                // Everything is as expected
            }
            else
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected TaskScheduler.Current context: Expected={1} Actual={2}",
                                  what, activationTaskScheduler, TaskScheduler.Current));
            }
        }

        private void LogStatus(string what)
        {
            logger.LogInformation(
                "{TimerName} Tick # {TickCount} - {Step} - RuntimeContext.Current={RuntimeContext} TaskScheduler.Current={TaskScheduler} CurrentWorkerThread={Thread}",
                timerName,
                tickCount,
                what,
                RuntimeContext.Current,
                TaskScheduler.Current,
                Thread.CurrentThread.Name);
        }
    }

    public class PocoTimerRequestGrain : IGrainBase, IPocoTimerRequestGrain
    {
        private TaskCompletionSource<int> completionSource;
        private List<TaskCompletionSource<(object, CancellationToken)>> _allTimerCallsTasks;

        public IGrainContext GrainContext { get; }

        public PocoTimerRequestGrain(IGrainContext grainContext)
        {
            GrainContext = grainContext;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(GrainContext.GrainId.ToString());
        }

        public async Task StartAndWaitTimerTick(TimeSpan dueTime)
        {
            this.completionSource = new TaskCompletionSource<int>();
            using var timer = this.RegisterGrainTimer(TimerTick, new() { DueTime = dueTime, Period = Timeout.InfiniteTimeSpan, Interleave = true });
            await this.completionSource.Task;
        }

        public Task StartStuckTimer(TimeSpan dueTime)
        {
            this.completionSource = new TaskCompletionSource<int>();
            var timer = this.RegisterGrainTimer(StuckTimerTick, new() { DueTime = dueTime, Period = TimeSpan.FromSeconds(1), Interleave = true });
            return Task.CompletedTask;
        }

        private Task TimerTick()
        {
            this.completionSource.TrySetResult(1);
            return Task.CompletedTask;
        }

        private async Task StuckTimerTick(CancellationToken cancellationToken)
        {
            await completionSource.Task;
        }

        public Task<int> TestAllTimerOverloads()
        {
            var tasks = new List<TaskCompletionSource<(object, CancellationToken)>>();
            var timers = new List<IGrainTimer>();

            // protected IGrainTimer RegisterGrainTimer(Func<Task> callback, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(() =>
            {
                tasks[0].TrySetResult(("NONE", CancellationToken.None));
                return Task.CompletedTask;
            }, new GrainTimerCreationOptions(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)) { Interleave = true }));

            // protected IGrainTimer RegisterGrainTimer(Func<Task> callback, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(() =>
            {
                tasks[1].TrySetResult(("NONE", CancellationToken.None));
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)));

            // protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, Task> callback, TState state, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(state =>
            {
                tasks[2].TrySetResult((state, CancellationToken.None));
                return Task.CompletedTask;
            },
            "STATE",
            new GrainTimerCreationOptions(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)) { Interleave = true }));

            // protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(state =>
            {
                tasks[3].TrySetResult((state, CancellationToken.None));
                return Task.CompletedTask;
            },
            "STATE",
            TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)));

            // With CancellationToken
            // protected IGrainTimer RegisterGrainTimer(Func<CancellationToken, Task> callback, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(ct =>
            {
                tasks[4].TrySetResult(("NONE", ct));
                return Task.CompletedTask;
            }, new GrainTimerCreationOptions(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)) { Interleave = true }));

            // protected IGrainTimer RegisterGrainTimer(Func<CancellationToken, Task> callback, TimeSpan dueTime, TimeSpan period)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer(ct =>
            {
                tasks[5].TrySetResult(("NONE", ct));
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)));

            // protected internal IGrainTimer RegisterGrainTimer<TState>(Func<TState, CancellationToken, Task> callback, TState state, GrainTimerCreationOptions options)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer((state, ct) =>
            {
                tasks[6].TrySetResult((state, ct));
                return Task.CompletedTask;
            },
            "STATE",
            new GrainTimerCreationOptions(TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)) { Interleave = true }));

            // protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, CancellationToken, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
            tasks.Add(new());
            timers.Add(this.RegisterGrainTimer((state, ct) =>
            {
                tasks[7].TrySetResult((state, ct));
                return Task.CompletedTask;
            },
            "STATE",
            TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(10)));
            _allTimerCallsTasks = tasks;
            return Task.FromResult(_allTimerCallsTasks.Count);
        }

        public Task<int> PollCompletedTimers() => Task.FromResult(_allTimerCallsTasks.Count(c => c.Task.IsCompleted));
        public async Task TestCompletedTimerResults()
        {
            var countWithState = 0;
            var countWithCancellation = 0;

            foreach (var task in _allTimerCallsTasks.Select(t => t.Task))
            {
                var (state, ct) = await task;
                var stateString  = Assert.IsType<string>(state);
                var hasState = string.Equals("STATE", stateString, StringComparison.Ordinal);
                if (hasState)
                {
                    countWithState++;
                }

                Assert.True(hasState || string.Equals("NONE", stateString, StringComparison.Ordinal));
                if (ct.CanBeCanceled)
                {
                    countWithCancellation++;
                }
            }

            Assert.Equal(4, countWithState);
            Assert.Equal(4, countWithCancellation);
        }
    }

    public class PocoNonReentrantTimerCallGrain : IGrainBase, IPocoNonReentrantTimerCallGrain
    {
        private readonly Dictionary<string, IGrainTimer> _timers = [];
        private int _tickCount;
        private Exception _tickException;
        private IGrainContext _context;
        private TaskScheduler _activationTaskScheduler;
        private Guid _tickId;

        private readonly ILogger _logger;
        private readonly IGrainFactory _grainFactory;

        public IGrainContext GrainContext { get; }

        public PocoNonReentrantTimerCallGrain(ILoggerFactory loggerFactory, IGrainContext grainContext, IGrainFactory grainFactory)
        {
            GrainContext = grainContext;
            _grainFactory = grainFactory;
            _logger = loggerFactory.CreateLogger($"{GetType().Name}-{GrainContext.GrainId}");
        }

        public Task<int> GetTickCount() => Task.FromResult(_tickCount);
        public Task<Exception> GetException() => Task.FromResult(_tickException);

        public async Task ExternalTick(string name)
        {
            await ProcessTimerTick(name, CancellationToken.None);
        }

        public Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _context = RuntimeContext.Current;
            _activationTaskScheduler = TaskScheduler.Current;
            return Task.CompletedTask;
        }

        public async Task RunSelfDisposingTimer()
        {
            var tcs = new TaskCompletionSource();
            var timer = new IGrainTimer[1];
            timer[0] = this.RegisterGrainTimer(async () =>
            {
                try
                {
                    Assert.NotNull(timer[0]);
                    timer[0].Dispose();
                    tcs.TrySetResult();
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            new GrainTimerCreationOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            {
                Interleave = true
            });

            await tcs.Task;
        }

        public Task StartTimer(string name, TimeSpan delay, bool keepAlive)
        {
            _logger.LogInformation("StartTimer Name={Name} Delay={Delay}", name, delay);
            if (_timers.TryGetValue(name, out var timer))
            {
                // Make the timer fire again after the specified delay.
                timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timers[name] = this.RegisterGrainTimer(TimerTick, name, new() { DueTime = delay, Period = Timeout.InfiniteTimeSpan, KeepAlive = keepAlive }); // One shot timer
            }

            return Task.CompletedTask;
        }

        public Task StopTimer(string name)
        {
            _logger.LogInformation("StopTimer Name={Name}", name);

            if (!_timers.Remove(name, out var timer))
            {
                throw new ArgumentException($"Could not find a timer with name {name}.");
            }

            timer.Dispose();
            return Task.CompletedTask;
        }

        private async Task TimerTick(object data, CancellationToken cancellationToken)
        {
            try
            {
                await ProcessTimerTick(data, cancellationToken);
            }
            catch (Exception exc)
            {
                _tickException = exc;
                throw;
            }
        }

        private async Task ProcessTimerTick(object data, CancellationToken cancellationToken)
        {
            var timerName = (string)data;
            string step = "TimerTick";
            CheckReentrancy(step, Guid.Empty);
            var expectedTickId = _tickId = Guid.NewGuid();
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            ISimpleGrain grain = _grainFactory.GetGrain<ISimpleGrain>(0, SimpleGrain.SimpleGrainNamePrefix);

            LogStatus("Before grain call #1", timerName);
            await grain.SetA(_tickCount);
            step = "After grain call #1";
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            LogStatus("Before Delay", timerName);
            await Task.Delay(TimeSpan.FromSeconds(1));
            step = "After Delay";
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            LogStatus("Before grain call #2", timerName);
            await grain.SetB(_tickCount);
            step = "After grain call #2";
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            LogStatus("Before grain call #3", timerName);
            int res = await grain.GetAxB();
            step = "After grain call #3 - Result = " + res;
            LogStatus(step, timerName);
            CheckRuntimeContext(step);
            CheckReentrancy(step, expectedTickId);

            _tickCount++;
            _tickId = Guid.Empty;
        }

        private void CheckRuntimeContext(string what)
        {
            if (RuntimeContext.Current == null
                || !RuntimeContext.Current.Equals(_context))
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected activation context: Expected={1} Actual={2}",
                                  what, _context, RuntimeContext.Current));
            }
            if (TaskScheduler.Current.Equals(_activationTaskScheduler) && TaskScheduler.Current is ActivationTaskScheduler)
            {
                // Everything is as expected
            }
            else
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected TaskScheduler.Current context: Expected={1} Actual={2}",
                                  what, _activationTaskScheduler, TaskScheduler.Current));
            }
        }

        private void CheckReentrancy(string what, Guid expected)
        {
            if (_tickId != expected)
            {
                throw new InvalidOperationException(
                    $"{what} in timer callback with unexpected interleaving: Expected={expected} Actual={_tickId}");
            }
        }

        private void LogStatus(string what, string timerName)
        {
            _logger.LogInformation(
                "{TimerName} Tick # {TickCount} - {Step} - RuntimeContext.Current={RuntimeContext} TaskScheduler.Current={TaskScheduler} CurrentWorkerThread={Thread}",
                timerName,
                _tickCount,
                what,
                RuntimeContext.Current,
                TaskScheduler.Current,
                Thread.CurrentThread.Name);
        }
    }
}
