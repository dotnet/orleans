
namespace Orleans.Runtime
{
    /// <summary>
    /// Extensions for <see cref="ILifecycleParticipant{TLifecycleObservable}"/>.
    /// </summary>
    public static class LifecycleParticipantExtensions
    {
        /// <summary>
        /// Conforms components written to participate with any <see cref="ILifecycleObservable"/> to take part in specific lifecycles.
        /// </summary>
        /// <typeparam name="TLifecycle">The target lifecycle observer type.</typeparam>
        /// <param name="participant">The lifecycle participant.</param>
        /// <returns>An adapter wrapped around <paramref name="participant"/> which implements <see cref="ILifecycleParticipant{TLifecycle}"/>.</returns>
        public static ILifecycleParticipant<TLifecycle> ParticipateIn<TLifecycle>(this ILifecycleParticipant<ILifecycleObservable> participant)
            where TLifecycle : ILifecycleObservable
        {
            return new Bridge<TLifecycle>(participant);
        }

        /// <summary>
        /// Adapts one lifecycle participant to a lifecycle participant of another observer type.
        /// </summary>
        /// <typeparam name="TLifecycle"></typeparam>
        private class Bridge<TLifecycle> : ILifecycleParticipant<TLifecycle>
            where TLifecycle : ILifecycleObservable
        {
            private readonly ILifecycleParticipant<ILifecycleObservable> participant;
            public Bridge(ILifecycleParticipant<ILifecycleObservable> participant)
            {
                this.participant = participant;
            }

            public void Participate(TLifecycle lifecycle)
            {
                this.participant?.Participate(lifecycle);
            }
        }
    }
}
