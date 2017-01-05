using AdventureGrainInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace AdventureWebClient
{
    public class GameState
    {
        static GameState _instance = new GameState();

        static ConnectionMapping<string> _connections = new ConnectionMapping<string>();
        static Dictionary<string, GameObject> _game = new Dictionary<string, GameObject>();        

        public static GameState Instance
        {
            get
            {
                return _instance;
            }
        }

        public ConnectionMapping<string> Connections
        {
            get
            {
                return _connections;
            }
        }

        public Dictionary<string, GameObject> Game
        {
            get
            {
                return _game;
            }
        }


    }
}