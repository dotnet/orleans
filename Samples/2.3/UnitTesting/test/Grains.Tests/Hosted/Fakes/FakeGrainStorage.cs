using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;

namespace Grains.Tests.Hosted.Fakes
{
    public class FakeGrainStorage : IGrainStorage
    {
        public ConcurrentDictionary<Tuple<string, GrainReference>, IGrainState> Storage { get; } = new ConcurrentDictionary<Tuple<string, GrainReference>, IGrainState>();

        public Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Storage.TryRemove(Tuple.Create(grainType, grainReference), out _);
            return Task.CompletedTask;
        }

        public Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Storage.TryGetValue(Tuple.Create(grainType, grainReference), out grainState);
            return Task.CompletedTask;
        }

        public Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Storage[Tuple.Create(grainType, grainReference)] = grainState;
            return Task.CompletedTask;
        }
    }
}