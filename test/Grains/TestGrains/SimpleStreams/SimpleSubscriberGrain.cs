using Orleans.BroadcastChannel;

namespace UnitTests.Grains.BroadcastChannel
{
    public interface ISubscriberGrain : IGrainWithStringKey
    {
        Task<List<Exception>> GetErrors(ChannelId streamId);

        Task<List<int>> GetValues(ChannelId streamId);

        Task<int> GetOnPublishedCounter();

        Task ThrowsOnReceive(bool throwsOnReceive);
    }

    public interface ISimpleSubscriberGrain : ISubscriberGrain { }

    public interface IRegexNamespaceSubscriberGrain : ISubscriberGrain { }

    public abstract class SubscriberGrainBase : Grain, ISubscriberGrain, IOnBroadcastChannelSubscribed
    {
        private readonly Dictionary<ChannelId, List<int>> _values = new();
        private readonly Dictionary<ChannelId, List<Exception>> _errors = new();
        private int _onPublishedCounter = 0;
        private bool _throwsOnReceive = false;

        public Task<List<Exception>> GetErrors(ChannelId streamId) => _errors.TryGetValue(streamId, out var errors) ? Task.FromResult(errors) : Task.FromResult(new List<Exception>());
        public Task<List<int>> GetValues(ChannelId streamId) => _values.TryGetValue(streamId, out var values) ? Task.FromResult(values) : Task.FromResult(new List<int>());
        public Task<int> GetOnPublishedCounter() => Task.FromResult(_onPublishedCounter);

        public Task OnSubscribed(IBroadcastChannelSubscription streamSubscription)
        {
            streamSubscription.Attach<int>(item => OnPublished(streamSubscription.ChannelId, item), ex => OnError(streamSubscription.ChannelId, ex));
            return Task.CompletedTask;

            Task OnPublished(ChannelId id, int item)
            {
                _onPublishedCounter++;
                if (_throwsOnReceive)
                {
                    throw new Exception("Some error message here");
                }
                if (!_values.TryGetValue(id, out var values))
                {
                    _values[id] = values = new List<int>();
                }
                values.Add(item);
                return Task.CompletedTask;
            }

            Task OnError(ChannelId id, Exception ex)
            {
                if (!_errors.TryGetValue(id, out var errors))
                {
                    _errors[id] = errors = new List<Exception>();
                }
                errors.Add(ex);
                return Task.CompletedTask;
            }
        }

        public Task ThrowsOnReceive(bool throwsOnReceive)
        {
            _throwsOnReceive = throwsOnReceive;
            return Task.CompletedTask;
        }
    }

    [ImplicitChannelSubscription]
    public class SimpleSubscriberGrain : SubscriberGrainBase, ISimpleSubscriberGrain { }

    [RegexImplicitChannelSubscription("multiple-namespaces-(.)+")]
    public class RegexNamespaceSubscriberGrain : SubscriberGrainBase, IRegexNamespaceSubscriberGrain { }
}
