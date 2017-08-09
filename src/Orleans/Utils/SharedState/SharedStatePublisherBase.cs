
using System.Threading;

namespace Orleans
{
    public class SharedStatePublisherBase<T> : ISharedStatePublisher<T>
    {
        protected SharedState<T> currentState;

        public SharedStatePublisherBase(T startingState)
        {
            this.currentState = new SharedState<T>(startingState);
        }

        public void Publish(T state)
        {
            var newSateChange = new SharedState<T>(state);
            SharedState<T> last = Interlocked.Exchange(ref this.currentState, newSateChange);
            last.NextCompletion.TrySetResult(newSateChange);
        }
    }
}
