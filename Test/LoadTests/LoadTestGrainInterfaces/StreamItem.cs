using System;
using Orleans.Concurrency;

namespace LoadTestGrainInterfaces
{
    [Serializable]
    [Immutable]
    public class StreamItem
    {
        public byte[] Data;

        public StreamItem(byte[] data)
        {
            Data = data;
        }
    }
}