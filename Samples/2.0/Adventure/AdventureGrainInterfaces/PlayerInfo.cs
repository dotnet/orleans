using Orleans.Concurrency;
using System;

namespace AdventureGrainInterfaces
{
    [Immutable]
    public class PlayerInfo
    {
        public Guid Key { get; set; }
        public string Name { get; set; }
    }
}
