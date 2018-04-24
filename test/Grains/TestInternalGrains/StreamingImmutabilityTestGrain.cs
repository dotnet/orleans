﻿using System;
using System.Threading.Tasks;
using Orleans;
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
            var stream = GetStreamProvider(providerName).GetStream<StreamImmutabilityTestObject>(guid, "Namespace");
            _streamSubscriptionHandle = await stream.SubscribeAsync(OnNextAsync);
        }

        public async Task UnsubscribeFromStream()
        {
            if (_streamSubscriptionHandle != null)
                await _streamSubscriptionHandle.UnsubscribeAsync();
        }

        public async Task SendTestObject(string providerName)
        {
            var stream = GetStreamProvider(providerName).GetStream<StreamImmutabilityTestObject>(this.GetPrimaryKey(), "Namespace");
            await stream.OnNextAsync(_myObject);
        }

        public Task SetTestObjectStringProperty(string value)
        {
            if(_myObject == null)
                _myObject = new StreamImmutabilityTestObject();

            _myObject.MyString = value;
            return Task.CompletedTask;
        }

        public Task<string> GetTestObjectStringProperty()
        {
            return Task.FromResult(_myObject.MyString);
        }

        public Task<string> GetSiloIdentifier()
        {
            return Task.FromResult(this.Runtime.SiloIdentity);
        }

        private Task OnNextAsync(StreamImmutabilityTestObject myObject, StreamSequenceToken streamSequenceToken)
        {
            _myObject = myObject;
            return Task.CompletedTask;
        }
    }

    [Serializable]
    public class StreamImmutabilityTestObject
    {
        public string MyString;
    }
}