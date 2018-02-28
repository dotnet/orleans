using Orleans.Concurrency;
using System;
using System.Collections.Generic;

namespace AdventureGrainInterfaces
{
    [Immutable]
    public class Thing
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public long FoundIn { get; set; }
        public List<String> Commands { get; set; }
    }

    [Immutable]
    public class MapInfo
    {
        public string Name { get; set; }
        public List<RoomInfo> Rooms { get; set; }
        public List<CategoryInfo> Categories { get; set; }
        public List<Thing> Things { get; set; }
        public List<MonsterInfo> Monsters { get; set; }
    }

    [Immutable]
    public class RoomInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, long> Directions { get; set; }
    }

    [Immutable]
    public class CategoryInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public List<String> Commands { get; set; }
    }
}
