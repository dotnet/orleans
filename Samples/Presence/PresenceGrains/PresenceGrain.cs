using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Samples.Presence.GrainInterfaces;

namespace PresenceGrains
{
    /// <summary>
    /// Stateless grain that decodes binary blobs and routes then to the appropriate game grains based on the blob content.
    /// Simulates how a cloud service receives raw data from a device and needs to preprocess it before forwarding for the actial computation.
    /// </summary>
    [StatelessWorker]
    public class PresenceGrain : Grain, IPresenceGrain
    {
        public Task Heartbeat(byte[] data)
        {
            HeartbeatData heartbeatData = HeartbeatDataDotNetSerializer.Deserialize(data);
            IGameGrain game = GrainFactory.GetGrain<IGameGrain>(heartbeatData.Game);
            return game.UpdateGameStatus(heartbeatData.Status);
        }
    }
}
