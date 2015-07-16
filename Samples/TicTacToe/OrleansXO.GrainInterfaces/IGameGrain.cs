/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
