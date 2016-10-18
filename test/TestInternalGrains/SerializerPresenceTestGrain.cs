using System;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    class SerializerPresenceTestGrain : Grain, ISerializerPresenceTest
    {
        public Task<bool> SerializerExistsForType(Type t)
        {
            return Task.FromResult(Orleans.Serialization.SerializationManager.HasSerializer(t));
        }

        public Task TakeSerializedData(object data)
        {
            // nothing to do
            return TaskDone.Done;
        }
    }
}
