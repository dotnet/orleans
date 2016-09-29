using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans;
using Orleans.Concurrency;
using OrleansXO.GrainInterfaces;

namespace OrleansXO.Grains
{
    /// <summary>
    /// Orleans grain implementation class GameGrain
    /// </summary>
    [Reentrant]
    public class GameGrain : Grain, IGameGrain
    {
        // list of players in the current game
        // for simplicity, player 0 always plays an "O" and player 1 plays an "X"
        //  who starts a game is a random call once a game is started, and is set via indexNextPlayerToMove
        public List<Guid> ListOfPlayers { get; private set; }
        private Int32 indexNextPlayerToMove;
        private Guid gameId;

        public GameState GameState { get; private set; }
        public Guid WinnerId { get; private set; }  // set when game is over
        public Guid LoserId { get; private set; }   // set when game is over

        // we record a game in terms of each of the moves, so we could reconstruct the sequence of play
        // during an active game, we also use a 2D array to represent the board, to make it
        //  easier to check for legal moves, wining lines, etc. 
        //  -1 represents an empty square, 0 & 1 the player's index 
        public List<GameMove> ListOfMoves { get; private set; }
        private int[,] theBoard;

        private string name;

        // initialise 
        public override Task OnActivateAsync()
        {
            // make sure newly formed game is in correct state 
            this.ListOfPlayers = new List<Guid>();
            this.ListOfMoves = new List<GameMove>();
            this.indexNextPlayerToMove = -1;  // safety default - is set when game begins to 0 or 1
            this.theBoard = new int[3, 3] { { -1, -1, -1 }, { -1, -1, -1 }, { -1, -1, -1 } };  // -1 is empty

            this.GameState = GameState.AwaitingPlayers;
            this.WinnerId = Guid.Empty;
            this.LoserId = Guid.Empty;

            this.gameId = this.GetPrimaryKey();

            return base.OnActivateAsync();
        }

        // add a player into a game
        public Task<GameState> AddPlayerToGame(Guid player)
        {
            // check if its ok to join this game
            if (this.GameState == GameState.Finished) throw new ApplicationException("Can't join game once its over");
            if (this.GameState == GameState.InPlay) throw new ApplicationException("Can't join game once its in play");

            // add player
            this.ListOfPlayers.Add(player);

            // check if the game is ready to play
            if (this.GameState == GameState.AwaitingPlayers && this.ListOfPlayers.Count == 2)
            {
                // a new game is starting
                this.GameState = GameState.InPlay;
                this.indexNextPlayerToMove = new Random().Next(0, 1);  // random as to who has the first move
            }

            // let user know if game is ready or not
            return Task.FromResult(this.GameState);
        }


        // make a move during the game
        public async Task<GameState> MakeMove(GameMove move)
        {
            // check if its a legal move to make
            if (this.GameState != GameState.InPlay) throw new ApplicationException("This game is not in play");

            if (ListOfPlayers.IndexOf(move.PlayerId) < 0) throw new ArgumentException("No such playerid for this game", "move");
            if (move.PlayerId != ListOfPlayers[indexNextPlayerToMove]) throw new ArgumentException("The wrong player tried to make a move", "move");

            if (move.X < 0 || move.X > 2 || move.Y < 0 || move.Y > 2) throw new ArgumentException("Bad co-ordinates for a move", "move");
            if (theBoard[move.X, move.Y] != -1) throw new ArgumentException("That square is not empty", "move");

            // record move
            this.ListOfMoves.Add(move);
            this.theBoard[move.X, move.Y] = indexNextPlayerToMove;

            // check for a winning move
            var win = false;
            if (!win)
                for (int i = 0; i < 3 && !win; i++)
                    win = isWinningLine(theBoard[i, 0], theBoard[i, 1], theBoard[i, 2]);
            if (!win)
                for (int i = 0; i < 3 && !win; i++)
                    win = isWinningLine(theBoard[0, i], theBoard[1, i], theBoard[2, i]);
            if (!win)
                win = isWinningLine(theBoard[0, 0], theBoard[1, 1], theBoard[2, 2]);
            if (!win)
                win = isWinningLine(theBoard[0, 2], theBoard[1, 1], theBoard[2, 0]);

            // check for draw

            var draw = false;
            if (this.ListOfMoves.Count() == 9)
                draw = true;  // we could try to look for stalemate earlier, if we wanted 

            // handle end of game
            if (win || draw)
            {
                // game over
                this.GameState = GameState.Finished;
                if (win)
                {
                    WinnerId = ListOfPlayers[indexNextPlayerToMove];
                    LoserId = ListOfPlayers[(indexNextPlayerToMove + 1) % 2];
                }

                // collect tasks up, so we await both notifications at the same time
                var promises = new List<Task>();
                // inform this player of outcome
                var playerGrain = GrainFactory.GetGrain<IPlayerGrain>(ListOfPlayers[indexNextPlayerToMove]);
                promises.Add(playerGrain.LeaveGame(this.GetPrimaryKey(), win ? GameOutcome.Win : GameOutcome.Draw));

                // inform other player of outcome
                playerGrain = GrainFactory.GetGrain<IPlayerGrain>(ListOfPlayers[(indexNextPlayerToMove + 1) % 2]);
                promises.Add(playerGrain.LeaveGame(this.GetPrimaryKey(), win ? GameOutcome.Lose : GameOutcome.Draw));
                await Task.WhenAll(promises);
                return this.GameState;
            }

            // if game hasnt ended, prepare for next players move
            indexNextPlayerToMove = (indexNextPlayerToMove + 1) % 2;
            return this.GameState;
        }

        private Boolean isWinningLine(int i, int j, int k)
        {
            if (i == 0 && j == 0 && k == 0) return true;
            if (i == 1 && j == 1 && k == 1) return true;
            return false;
        }


        public Task<GameState> GetState()
        {
            return Task.FromResult(this.GameState);
        }

        public Task<List<GameMove>> GetMoves()
        {
            return Task.FromResult(this.ListOfMoves);
        }

        public async Task<GameSummary> GetSummary(Guid player)
        {
            var promises = new List<Task<string>>();
            foreach (var p in this.ListOfPlayers.Where(p => p != player))
            {
                promises.Add(GrainFactory.GetGrain<IPlayerGrain>(p).GetUsername());
            }
            await Task.WhenAll(promises);

            return new GameSummary
            {
                NumMoves = this.ListOfMoves.Count,
                State = this.GameState,
                YourMove = this.GameState == GameState.InPlay && player == this.ListOfPlayers[this.indexNextPlayerToMove],
                NumPlayers = this.ListOfPlayers.Count,
                GameId = this.GetPrimaryKey(),
                Usernames = promises.Select(x => x.Result).ToArray(),
                Name = this.name,
                GameStarter = this.ListOfPlayers.FirstOrDefault() == player
            };
        }

        public Task SetName(string name)
        {
            this.name = name;
            return TaskDone.Done;
        }


    }
}
