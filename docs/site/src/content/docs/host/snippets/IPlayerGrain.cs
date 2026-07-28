namespace Client;

public interface IPlayerGrain : IGrainWithGuidKey
{
    Task<GameState> JoinGame(Guid gameId);

    Task<IGameGrain?> GetCurrentGame();

    Task JoinGame(IGameGrain game);

    Task LeaveGame(IGameGrain game);
}
