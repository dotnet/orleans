using Orleans;

namespace Presence.Grains;

/// <summary>
/// Interface to an individual player that may or may not be in a game at any point in time.
/// </summary>
public interface IPlayerGrain : IGrainWithGuidKey
{
    Task<IGameGrain?> GetCurrentGameAsync();
    Task JoinGameAsync(IGameGrain game);
    Task LeaveGameAsync(IGameGrain game);
}
