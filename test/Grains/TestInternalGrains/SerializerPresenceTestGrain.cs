using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    class SerializerPresenceTestGrain : Grain, ISerializerPresenceTest
    {
        public Task<bool> SerializerExistsForType(Type t)
        {
            return Task.FromResult(this.ServiceProvider.GetRequiredService<Serializer>().CanSerialize(t));
        }

        public Task TakeSerializedData(object data)
        {
            // nothing to do
            return Task.CompletedTask;
        }
    }
}
