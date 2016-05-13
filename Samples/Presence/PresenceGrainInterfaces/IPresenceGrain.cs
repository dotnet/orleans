using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace Orleans.Samples.Presence.GrainInterfaces
{
    /// <summary>
    /// Defines an interface for sending binary updates without knowing the specific game ID.
    /// Simulates what game consoles do when they send data to the cloud.
    /// </summary>
    public interface IPresenceGrain : IGrainWithIntegerKey
    {
        Task Heartbeat(byte[] data);
    }
}
