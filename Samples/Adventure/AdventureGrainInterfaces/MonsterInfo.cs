using System;
using System.Collections.Generic;

using Orleans;
using Orleans.Concurrency;

namespace AdventureGrainInterfaces
{
    [Immutable]
    public class MonsterInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public List<long> KilledBy { get; set; }
    }
}
