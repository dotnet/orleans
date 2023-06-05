using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class StreamingImmutabilityTestGrain : Grain, IStreamingImmutabilityTestGrain
    {
        private StreamImmutabilityTestObject _myObject;
        private StreamSubscriptionHandle<StreamImmutabilityTestObject> _streamSubscriptionHandle;

        public async Task SubscribeToStream(Guid guid, string providerName)
        {
            var stream = this.GetStreamProvider(providerName).GetStream<StreamImmutabilityTestObject>("Namespace", guid);
            _streamSubscriptionHandle = await stream.SubscribeAsync(OnNextAsync);
        }

        public async Task UnsubscribeFromStream()
        {
            if (_streamSubscriptionHandle != null)
                await _streamSubscriptionHandle.UnsubscribeAsync();
        }

        public async Task SendTestObject(string providerName)
        {
            var stream = this.GetStreamProvider(providerName).GetStream<StreamImmutabilityTestObject>("Namespace", this.GetPrimaryKey());
            await stream.OnNextAsync(_myObject);
        }

        public Task SetTestObjectStringProperty(string value)
        {
            _myObject ??= new StreamImmutabilityTestObject();

            _myObject.MyString = value;
            return Task.CompletedTask;
        }

        public Task<string> GetTestObjectStringProperty() => Task.FromResult(_myObject.MyString);

        public Task<string> GetSiloIdentifier() => Task.FromResult(Runtime.SiloIdentity);

        private Task OnNextAsync(StreamImmutabilityTestObject myObject, StreamSequenceToken streamSequenceToken)
        {
            _myObject = myObject;
            return Task.CompletedTask;
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class StreamImmutabilityTestObject
    {
        [Id(0)]
        public string MyString;
    }
}