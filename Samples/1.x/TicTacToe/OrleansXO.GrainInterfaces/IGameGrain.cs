using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Orleans;

namespace OrleansXO.GrainInterfaces
{
    public interface IGameGrain : IGrainWithGuidKey
    {
        // add a player into a game
        Task<GameState> AddPlayerToGame(Guid player);
        Task<GameState> GetState();
        Task<List<GameMove>> GetMoves();
        Task<GameState> MakeMove(GameMove move);
        Task<GameSummary> GetSummary(Guid player);
        Task SetName(string name);
    }


    // define the possible states a game can be in
    public enum GameState
    {
        AwaitingPlayers,
        InPlay,
        Finished
    }


    // define game outcomes
    public enum GameOutcome
    {
        Win,
        Lose,
        Draw
    }


    public struct GameMove
    {
        public Guid PlayerId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

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
}
