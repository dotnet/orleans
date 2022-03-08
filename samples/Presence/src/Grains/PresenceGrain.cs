using Orleans;
using Orleans.Concurrency;
using Presence.Grains.Models;

namespace Presence.Grains;

/// <summary>
/// Stateless grain that decodes binary blobs and routes then to the appropriate game grains based on the blob content.
/// Simulates how a cloud service receives raw data from a device and needs to preprocess it before forwarding for the actial computation.
/// </summary>
[StatelessWorker]
public class PresenceGrain : Grain, IPresenceGrain
{
    public Task HeartbeatAsync(byte[] data)
    {
        var heartbeatData = HeartbeatDataDotNetSerializer.Deserialize(data);
        var game = GrainFactory.GetGrain<IGameGrain>(heartbeatData.GameKey);
        return game.UpdateGameStatusAsync(heartbeatData.Status);
    }
}
