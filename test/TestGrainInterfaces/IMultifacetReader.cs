﻿using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces

{
    public interface IMultifacetReader : IGrainWithIntegerKey
    {
        Task<int> GetValue();
        //event ValueUpdateEventHandler ValueUpdateEvent;
        //event ValueUpdateEventHandler CommonEvent;
    }
}
