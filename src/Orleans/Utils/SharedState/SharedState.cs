
using System.Threading.Tasks;

namespace Orleans
{
    public interface ISharedState<T>
    {
        T State { get; }
        Task<ISharedState<T>> NextAsync { get; }
    }

    public class SharedState<T> : ISharedState<T>
    {
        public SharedState(T state)
        {
            this.State = state;
            this.NextCompletion = new TaskCompletionSource<ISharedState<T>>();
        }

        public SharedState(SharedState<T> sharedState)
        {
            this.State = sharedState.State;
            this.NextCompletion = sharedState.NextCompletion;
        }

        public T State { get; }
        public Task<ISharedState<T>> NextAsync => this.NextCompletion.Task;
        public TaskCompletionSource<ISharedState<T>> NextCompletion { get; }
    }
}
