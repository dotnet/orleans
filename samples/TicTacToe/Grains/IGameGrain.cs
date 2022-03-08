using Orleans;

namespace TicTacToe.Grains;

public interface IGameGrain : IGrainWithGuidKey
{
    Task<GameState> AddPlayerToGame(Guid player);
    Task<GameState> GetState();
    Task<List<GameMove>> GetMoves();
    Task<GameState> MakeMove(GameMove move);
    Task<GameSummary> GetSummary(Guid player);
    Task SetName(string name);
}

[Serializable]
public enum GameState
{
    AwaitingPlayers,
    InPlay,
    Finished
}

[Serializable]
public enum GameOutcome
{
    Win,
    Lose,
    Draw
}

[Serializable]
public struct GameMove
{
    public Guid PlayerId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

[Serializable]
public struct GameSummary
{
    public GameState State { get; set; }
    public bool YourMove { get; set; }
    public int NumMoves { get; set; }
    public GameOutcome Outcome { get; set; }
    public int NumPlayers { get; set; }
    public Guid GameId { get; set; }
    public string[] Usernames { get; set; }
    public string Name { get; set; }
    public bool GameStarter { get; set; }
}
