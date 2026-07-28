namespace Client;

public interface IGameGrain : IGrainWithGuidKey
{
    Task UpdateGameStatus(GameState state);

    Task ObserveGameUpdates(IGameObserver observer);
    
    Task UnobserveGameUpdates(IGameObserver observer);
}
