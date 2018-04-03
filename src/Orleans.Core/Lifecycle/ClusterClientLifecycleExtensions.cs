
namespace Orleans.Runtime
{
    public static class LifecycleParticipantExtensions
    {
        /// <summary>
        /// Conforms components written to participate with any ILifecycleObservable to take part in specific lifecycles
        /// </summary>
        /// <typeparam name="TLifecycle"></typeparam>
        /// <param name="participant"></param>
        /// <returns></returns>
        public static ILifecycleParticipant<TLifecycle> ParticipateIn<TLifecycle>(this ILifecycleParticipant<ILifecycleObservable> participant)
            where TLifecycle : ILifecycleObservable
        {
            return new Bridge<TLifecycle>(participant);
        }

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
