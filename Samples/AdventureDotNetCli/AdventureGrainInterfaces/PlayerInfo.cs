using System;
using Orleans;
using Orleans.Concurrency;

namespace AdventureGrainInterfaces
{
    [Immutable]
    public class PlayerInfo
    {
        public Guid Key { get; set; }
        public string Name { get; set; }
    }
}
