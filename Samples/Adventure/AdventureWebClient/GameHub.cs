using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Microsoft.AspNet.SignalR;
using System.Threading.Tasks;
using System.Diagnostics;
using Orleans;
using AdventureGrainInterfaces;
using System.Net;

namespace AdventureWebClient
{

    // Most code from https://www.asp.net/signalr/overview/getting-started/tutorial-getting-started-with-signalr

    public class GameHub : Hub
    {

   
        #region Security
        public override async Task OnConnected()
        {
            string userID = Context.QueryString["userid"];

            GameState.Instance.Connections.Add(userID, Context.ConnectionId);
            Debug.WriteLine("+# connections = " + GameState.Instance.Connections.Count);

            await addNewPlayer(userID);

            await base.OnConnected();
        }



        public override async Task OnDisconnected(bool stopCalled)
        {

            string userID = Context.QueryString["userid"];

            GameState.Instance.Connections.Remove(userID, Context.ConnectionId);

            await KillPlayer(userID);

            Debug.WriteLine("-# connections = " + GameState.Instance.Connections.Count);

            await base.OnDisconnected(stopCalled);
        }


        public override async Task OnReconnected()
        {
            string userID = Context.QueryString["userid"];

            if (!GameState.Instance.Connections.GetConnections(userID).Contains(Context.ConnectionId))
            {
                GameState.Instance.Connections.Add(userID, Context.ConnectionId);
                await addNewPlayer(userID);
            }

            Debug.WriteLine("r# connections = " + GameState.Instance.Connections.Count);
            await base.OnReconnected();
        }
        #endregion

        #region API called from WebClient
        // Hub function called by Web Client
        public async Task Send(string message)
        {            
            // Call the AddResponse method to update client.            
            await this.ProcessMessage(message);
        }
        #endregion

        #region Game related functions

        private async Task<bool> addNewPlayer(string name)
        {
            GameObject game;            
            game = findGame(name);

            if (game == null)
            {
                game = new GameObject(name);

                // Create player
                Guid playerGuid = Guid.NewGuid();
                game.Player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerGuid);

                var message = new Message(Clients.Caller, name);
                //Create a reference for Message usable for subscribing to the observable grain.
                IMessage messageInterface = await GrainClient.GrainFactory.CreateObjectReference<IMessage>(message);
                await game.Player.Subscribe(messageInterface);

                // Save game information to dictionary
                GameState.Instance.Game.Add(name, game);

                // Login to Adventure
                game.Player.SetName(name).Wait();

                var room1 = GrainClient.GrainFactory.GetGrain<IRoomGrain>(0);
                game.Player.SetRoomGrain(room1).Wait();

                Clients.Caller.AddResponse(@"
  ___      _                 _                  
 / _ \    | |               | |                 
/ /_\ \ __| |_   _____ _ __ | |_ _   _ _ __ ___ 
|  _  |/ _` \ \ / / _ \ '_ \| __| | | | '__/ _ \
| | | | (_| |\ V /  __/ | | | |_| |_| | | |  __/
\_| |_/\__,_| \_/ \___|_| |_|\__|\__,_|_|  \___|");

                Clients.Caller.AddResponse(string.Format("\r\nWelcome to {0} !\r\n", Dns.GetHostName()));
                Clients.Caller.AddResponse(string.Format("It is {0} now !\r\n\r\n", DateTime.Now));

            }

            if (game.Player != null)
            {
                string response = await game.Player.Play("look");
                Clients.Caller.AddResponse(response);
            }

            return true;
        }
        private GameObject findGame(string name)
        {
            try
            {
                if (GameState.Instance.Game.ContainsKey(name))
                {
                    return GameState.Instance.Game[name];
                }
                else
                {
                    return null;
                }
            }
            catch (Exception)
            {
                return null;
            }

        }

        private bool SendMessageToClient(string userid, string message)
        {

            foreach (var connectionId in GameState.Instance.Connections.GetConnections(userid))
            {
                var v = Clients.Client(connectionId);
                v.AddResponse(message);
            }
            return true;
        }

        private async Task<bool> KillPlayer(string name)
        {
            GameObject game = findGame(name);
            if (game == null) return true;
            
            await game.Player.Play("End"); // disconnect

            await game.Player.UnSubscribe(game.messageInterface);
            await GrainClient.GrainFactory.DeleteObjectReference<IMessage>(game.messageInterface);

            game.Player = null;
            game.messageInterface = null;
            game = null;

            GameState.Instance.Game.Remove(name);            

            return true;
        }


        private async Task<bool> ProcessMessage(string msg)
        {
            string name = Context.QueryString["userid"];

            string response = "";

            if (string.IsNullOrEmpty(msg))
            {
                response = "Please type something.\r\n";
            }

            else if (string.Equals("End", msg, StringComparison.OrdinalIgnoreCase))
            {
                response = "Have a good day!\r\n";
            }
            else
            {
                GameObject game = findGame(name);

                if (game == null)
                {
                    await addNewPlayer(name); ;
                    game = findGame(name);

                }

                if (game.Player == null)
                {
                    response = "Unable to create client object";
                }
                else
                {
                    response = await game.Player.Play(msg);
                }
            }

            if (!string.IsNullOrEmpty(response))
            {
                Clients.Caller.AddResponse(response);
            }

            return true;
        }
        #endregion

    }

    public class Message : IMessage
    {
        Microsoft.AspNet.SignalR.Hubs.StatefulSignalProxy _client;
        string _userid;

        public Message(Microsoft.AspNet.SignalR.Hubs.StatefulSignalProxy client, string userid)
        {
            _client = client;
            _userid = userid;
        }
    
        public void ReceiveMessage(string message)
        {
           _client.Invoke("AddResponse",message);            
        }
    }
}