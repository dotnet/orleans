using Orleans;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
