
using Orleans;
using Orleans.Concurrency;

namespace TicTacToe.Grains;

/// <summary>
/// Orleans grain implementation class GameGrain
/// </summary>
[Reentrant]
public class GameGrain : Grain, IGameGrain
{
    // list of players in the current game
    // for simplicity, player 0 always plays an "O" and player 1 plays an "X"
    //  who starts a game is a random call once a game is started, and is set via indexNextPlayerToMove
    public List<Guid> _playerIds = new();
    private int _indexNextPlayerToMove;

    private GameState _gameState;
    private Guid _winnerId;
    private Guid _loserId;

    // we record a game in terms of each of the moves, so we could reconstruct the sequence of play
    // during an active game, we also use a 2D array to represent the board, to make it
    //  easier to check for legal moves, wining lines, etc. 
    //  -1 represents an empty square, 0 & 1 the player's index 
    private List<GameMove> _moves = new();
    private int[,] _board = null!;

    private string _name = null!;

    // initialise 
    public override Task OnActivateAsync()
    {
        // make sure newly formed game is in correct state 
        _playerIds = new List<Guid>();
        _moves = new List<GameMove>();
        _indexNextPlayerToMove = -1;  // safety default - is set when game begins to 0 or 1
        _board = new int[3, 3] { { -1, -1, -1 }, { -1, -1, -1 }, { -1, -1, -1 } };  // -1 is empty

        _gameState = GameState.AwaitingPlayers;
        _winnerId = Guid.Empty;
        _loserId = Guid.Empty;

        return base.OnActivateAsync();
    }

    // add a player into a game
    public Task<GameState> AddPlayerToGame(Guid player)
    {
        // check if its ok to join this game
        if (_gameState is GameState.Finished) throw new ApplicationException("Can't join game once its over");
        if (_gameState is GameState.InPlay) throw new ApplicationException("Can't join game once its in play");

        // add player
        _playerIds.Add(player);

        // check if the game is ready to play
        if (_gameState is GameState.AwaitingPlayers && _playerIds.Count is 2)
        {
            // a new game is starting
            _gameState = GameState.InPlay;
            _indexNextPlayerToMove = Random.Shared.Next(0, 1);  // random as to who has the first move
        }

        // let user know if game is ready or not
        return Task.FromResult(_gameState);
    }

    // make a move during the game
    public async Task<GameState> MakeMove(GameMove move)
    {
        // check if its a legal move to make
        if (_gameState is not GameState.InPlay) throw new ApplicationException("This game is not in play");

        if (_playerIds.IndexOf(move.PlayerId) < 0) throw new ArgumentException("No such playerid for this game", "move");
        if (move.PlayerId != _playerIds[_indexNextPlayerToMove]) throw new ArgumentException("The wrong player tried to make a move", "move");

        if (move.X < 0 || move.X > 2 || move.Y < 0 || move.Y > 2) throw new ArgumentException("Bad co-ordinates for a move", "move");
        if (_board[move.X, move.Y] != -1) throw new ArgumentException("That square is not empty", "move");

        // record move
        _moves.Add(move);
        _board[move.X, move.Y] = _indexNextPlayerToMove;

        // check for a winning move
        var win = false;
        for (var i = 0; i < 3 && !win; i++)
        {
            win = IsWinningLine(_board[i, 0], _board[i, 1], _board[i, 2]);
        }

        if (!win)
        {
            for (var i = 0; i < 3 && !win; i++)
            {
                win = IsWinningLine(_board[0, i], _board[1, i], _board[2, i]);
            }
        }

        if (!win)
        {
            win = IsWinningLine(_board[0, 0], _board[1, 1], _board[2, 2]);
        }

        if (!win)
        {
            win = IsWinningLine(_board[0, 2], _board[1, 1], _board[2, 0]);
        }

        // check for draw
        var draw = false;
        if (_moves.Count is 9)
        {
            draw = true;  // we could try to look for stalemate earlier, if we wanted 
        }

        // handle end of game
        if (win || draw)
        {
            // game over
            _gameState = GameState.Finished;
            if (win)
            {
                _winnerId = _playerIds[_indexNextPlayerToMove];
                _loserId = _playerIds[(_indexNextPlayerToMove + 1) % 2];
            }

            // collect tasks up, so we await both notifications at the same time
            var promises = new List<Task>();
            // inform this player of outcome
            var playerGrain = GrainFactory.GetGrain<IPlayerGrain>(_playerIds[_indexNextPlayerToMove]);
            promises.Add(playerGrain.LeaveGame(this.GetPrimaryKey(), win ? GameOutcome.Win : GameOutcome.Draw));

            // inform other player of outcome
            playerGrain = GrainFactory.GetGrain<IPlayerGrain>(_playerIds[(_indexNextPlayerToMove + 1) % 2]);
            promises.Add(playerGrain.LeaveGame(this.GetPrimaryKey(), win ? GameOutcome.Lose : GameOutcome.Draw));
            await Task.WhenAll(promises);
            return _gameState;
        }

        // if game hasnt ended, prepare for next players move
        _indexNextPlayerToMove = (_indexNextPlayerToMove + 1) % 2;
        return _gameState;
    }

    private static bool IsWinningLine(int i, int j, int k) => (i, j, k) switch
    {
        (0, 0, 0) => true,
        (1, 1, 1) => true,
        _ => false
    };


    public Task<GameState> GetState() => Task.FromResult(_gameState);

    public Task<List<GameMove>> GetMoves() => Task.FromResult(_moves);

    public async Task<GameSummary> GetSummary(Guid player)
    {
        var promises = new List<Task<string>>();
        foreach (var p in _playerIds.Where(p => p != player))
        {
            promises.Add(GrainFactory.GetGrain<IPlayerGrain>(p).GetUsername());
        }

        await Task.WhenAll(promises);

        return new GameSummary
        {
            NumMoves = _moves.Count,
            State = _gameState,
            YourMove = _gameState is GameState.InPlay && player == _playerIds[_indexNextPlayerToMove],
            NumPlayers = _playerIds.Count,
            GameId = this.GetPrimaryKey(),
            Usernames = promises.Select(x => x.Result).ToArray(),
            Name = _name,
            GameStarter = _playerIds.FirstOrDefault() == player
        };
    }

    public Task SetName(string name)
    {
        _name = name;
        return Task.CompletedTask;
    }
}
