using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Timers;

namespace Grains
{
    public class CounterGrain : Grain, ICounterGrain
    {
        private readonly IPersistentState<Counter> counter;
        private readonly ITimerRegistry timers;
        private readonly IReminderRegistry reminders;
        private readonly IGrainFactory hostedGrainFactory;

        /// <summary>
        /// Creates an instance of <see cref="CounterGrain"/>.
        /// Only use the constructor for isolated unit tests.
        /// </summary>
        /// <param name="counter">
        /// A grain may have multiple injected state items.
        /// This facilitates state sharding for smaller and faster reads and writes.
        /// This also allows easy mocking in isolated unit tests.
        /// </param>
        /// <param name="timers">
        /// A grain may request the timer registry and use it as-is to allow mocking via injection.
        /// The alternative is to open up the <see cref="RegisterTimer(Func{object, Task}, object, TimeSpan, TimeSpan)"/> method for mocking in the test.
        /// </param>
        /// <param name="reminders">
        /// A grain may request the reminder registry and use it as-is.
        /// The alternative is to open up the <see cref="RegisterOrUpdateReminder(string, TimeSpan, TimeSpan)"/> method for mocking in the test.
        /// </param>
        /// <param name="hostedFactory">
        /// A grain may request the hosted grain factory and use it as-is.
        /// The hosted grain factory also allows calling grains from outside the orleans task scheduler, e.g. from the thread pool.
        /// The alternative is to open up the <see cref="GrainFactory"/> property for mocking in the test.
        /// </param>
        public CounterGrain([PersistentState("Counter")] IPersistentState<Counter> counter, ITimerRegistry timers, IReminderRegistry reminders, IGrainFactory hostedFactory)
        {
            this.counter = counter;
            this.timers = timers;
            this.reminders = reminders;
            this.hostedGrainFactory = hostedFactory;
        }

        public override Task OnActivateAsync()
        {
            /// Set a timer to publish the running count to the summary grain.
            /// We are using the helper method here but we can also use the injected <see cref="ITimerRegistry"/> instance.
            RegisterTimer(_ =>

                hostedGrainFactory.GetGrain<ISummaryGrain>(Guid.Empty)
                    .SetAsync(GrainKey, counter.State.Value)

            , null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            return base.OnActivateAsync();
        }

        /// <summary>
        /// The "virtual" allows the unit test to set the key as needed.
        /// </summary>
        public virtual string GrainKey => this.GetPrimaryKeyString();

        /// <summary>
        /// This allows the isolated unit test to mock the provided grain factory at run time.
        /// The alternative is to request the hosted <see cref="IGrainFactory"/> via dependency injection and mock that instead.
        /// The hosted grain factory also allows calling grains from thread pool tasks.
        /// </summary>
        public new virtual IGrainFactory GrainFactory => base.GrainFactory;

        /// <summary>
        /// This allows the isolated unit test to mock the timer registration method.
        /// The alternative is to request the <see cref="ITimerRegistry"/> via dependency injection and mock that instead.
        /// </summary>
        public new virtual IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
            => base.RegisterTimer(asyncCallback, state, dueTime, period);

        /// <summary>
        /// This allows the isolated unit test to mock the reminder registration method.
        /// The alternative is to request the <see cref="IReminderRegistry"/> via dependency injection and mock that instead.
        /// </summary>
        public new virtual Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period) =>
            base.RegisterOrUpdateReminder(reminderName, dueTime, period);

        public Task IncrementAsync()
        {
            counter.State.Value += 1;

            return Task.CompletedTask;
        }

        public Task<int> GetValueAsync() => Task.FromResult(counter.State.Value);

        public Task SaveAsync() => counter.WriteStateAsync();

        public class Counter
        {
            public int Value { get; set; }
        }
    }
}