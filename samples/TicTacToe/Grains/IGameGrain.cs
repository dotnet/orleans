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

[GenerateSerializer]
public struct GameMove
{
    [Id(0)] public Guid PlayerId { get; set; }
    [Id(1)] public int X { get; set; }
    [Id(2)] public int Y { get; set; }
}

[GenerateSerializer]
public struct GameSummary
{
    [Id(0)]  public GameState State { get; set; }
    [Id(1)]  public bool YourMove { get; set; }
    [Id(2)]  public int NumMoves { get; set; }
    [Id(3)]  public GameOutcome Outcome { get; set; }
    [Id(4)]  public int NumPlayers { get; set; }
    [Id(5)]  public Guid GameId { get; set; }
    [Id(6)]  public string[] Usernames { get; set; }
    [Id(7)]  public string Name { get; set; }
    [Id(8)]  public bool GameStarter { get; set; }
}
