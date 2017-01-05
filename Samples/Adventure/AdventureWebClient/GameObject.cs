using AdventureGrainInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace AdventureWebClient
{
    public class GameObject
    {
        private string _name;

        public GameObject(string name)
        {
            _name = name;
        }

        public string Name { get { return _name; }}

        private IPlayerGrain _player;

        public IPlayerGrain Player
        {
            get { return _player; }
            set { _player = value; }
        }

        private IMessage _messageInterface;

        public IMessage  messageInterface
        {
            get { return _messageInterface; }
            set { _messageInterface = value; }
        }

         
    }
}