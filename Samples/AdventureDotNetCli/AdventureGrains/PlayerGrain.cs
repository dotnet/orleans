using AdventureGrainInterfaces;
using Orleans;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AdventureGrains
{
    public class PlayerGrain : Orleans.Grain, IPlayerGrain
    {
        IRoomGrain roomGrain; // Current room
        List<Thing> things = new List<Thing>(); // Things that the player is carrying
        private ObserverSubscriptionManager<IMessage> _subsManager;


        bool killed = false;

        PlayerInfo myInfo;


        public override Task OnActivateAsync()
        {
            this.myInfo = new PlayerInfo { Key = this.GetPrimaryKey(), Name = "nobody" };


            // Hook up stream where we push messages to Client
            _subsManager = new ObserverSubscriptionManager<IMessage>();

            return base.OnActivateAsync();
        }

        // Clients call this to subscribe.
        public Task Subscribe(IMessage observer)
        {
             _subsManager.Subscribe(observer);
            return TaskDone.Done;
        }

        //Also clients use this to unsubscribe themselves to no longer receive the messages.
        public Task UnSubscribe(IMessage observer)
        {
            _subsManager.Unsubscribe(observer);
            return TaskDone.Done;
        }

        public Task SendUpdateMessage(string message)
        {
            _subsManager.Notify(s => s.ReceiveMessage(message));
            return TaskDone.Done;
        }

        Task<string> IPlayerGrain.Name()
        {
            return Task.FromResult(myInfo.Name);
        }

        Task<IRoomGrain> IPlayerGrain.RoomGrain()
        {
            return Task.FromResult(roomGrain);
        }


        async Task IPlayerGrain.Die(PlayerInfo killer, Thing weapon)
        {
            await DropAllItems();

            // Exit the game
            if (this.roomGrain != null)
            {
                if (killer != null)
                {
                    // He was killed by a player
                    await this.roomGrain.ExitDead(myInfo, killer, weapon);
                    await SendUpdateMessage("You where killed by " + killer.Name + "!");
                }
                else
                {
                    await this.roomGrain.Exit(myInfo);
                    await SendUpdateMessage("You died!");
                }
                this.roomGrain = null;
                killed = true;


            }
        }

        private async Task DropAllItems()
        {
            // Drop everything
            var tasks = new List<Task<string>>();
            foreach (var thing in new List<Thing>(things))
            {
                tasks.Add(this.Drop(thing));
            }
            await Task.WhenAll(tasks);
        }

        async Task<string> Drop(Thing thing)
        {
            if (killed)
                return await CheckAlive();

            if (thing != null)
            {
                this.things.Remove(thing);
                await this.roomGrain.Drop(thing);
                return "Okay.";
            }
            else
                return "I don't understand.";
        }

        async Task<string> Take(Thing thing)
        {
            if (killed)
                return await CheckAlive();

            if (thing != null)
            {
                this.things.Add(thing);
                await this.roomGrain.Take(thing);
                return "Okay.";
            }
            else
                return "I don't understand.";
        }


        Task IPlayerGrain.SetName(string name)
        {
            this.myInfo.Name = name;
            return TaskDone.Done;
        }

        Task IPlayerGrain.SetRoomGrain(IRoomGrain room)
        {
            this.roomGrain = room;
            return room.Enter(myInfo);
        }

        async Task<string> Go(string direction)
        {
            IRoomGrain destination = await this.roomGrain.ExitTo(direction);

            StringBuilder description = new StringBuilder();

            if (destination != null)
            {
                await this.roomGrain.Exit(myInfo);
                await destination.Enter(myInfo);

                this.roomGrain = destination;
                var desc = await destination.Description(myInfo);

                if (desc != null)
                    description.Append(desc);
            }
            else
            {
                description.Append("You cannot go in that direction.");
            }

            if (things.Count > 0)
            {
                description.AppendLine("You are holding the following items:");
                foreach (var thing in things)
                {
                    description.AppendLine(thing.Name);
                }
            }

            return description.ToString();
        }

        async Task<string> CheckAlive()
        {
            if (!killed)
                return null;

            // Go to room '-2', which is the place of no return.
            var room = GrainFactory.GetGrain<IRoomGrain>(-2);
            return await room.Description(myInfo);
        }

        async Task<string> Kill(string target)
        {
            if (things.Count == 0)
                return "With what? Your bare hands?";

            var player = await this.roomGrain.FindPlayer(target);
            if (player != null)
            {
                if (player.Key  == myInfo.Key)
                {
                    return "You can't kill yourself!";                
                }

                var weapon = things.Where(t => t.Category == "weapon").FirstOrDefault();
                if (weapon != null)
                {
                    await GrainFactory.GetGrain<IPlayerGrain>(player.Key).Die(myInfo, weapon);
                    return target + " is now dead.";                    
                }
                return "With what? Your bare hands?";
            }

            var monster = await this.roomGrain.FindMonster(target);
            if (monster != null)
            {
                var weapons = monster.KilledBy.Join(things, id => id, t => t.Id, (id, t) => t);
                if (weapons.Count() > 0)
                {
                    Thing weapon = weapons.Where(t => t.Category == "weapon").FirstOrDefault();

                    await GrainFactory.GetGrain<IMonsterGrain>(monster.Id).Kill(this.roomGrain, player, weapon);
                    return target + " is now dead.";
                }
                return "With what? Your bare hands?";
            }
            return "I can't see " + target + " here. Are you sure?";
        }

        private string RemoveStopWords(string s)
        {
            string[] stopwords = new string[] { " on ", " the ", " a " };

            StringBuilder sb = new StringBuilder(s);
            foreach (string word in stopwords)
            {
                sb.Replace(word, " ");
            }

            return sb.ToString();
        }

        private Thing FindMyThing(string name)
        {
            return things.Where(x => x.Name == name).FirstOrDefault();
        }

        private string Rest(string[] words)
        {
            StringBuilder sb = new StringBuilder();

            for (int i = 1; i < words.Length; i++)
                sb.Append(words[i] + " ");

            return sb.ToString().Trim().ToLower();
        }

        async Task IPlayerGrain.SendMessage(string message)
        {
            await SendUpdateMessage(message);
        }

        async Task<string> Whisper(string words)
        {
            if (this.roomGrain != null)
            {
                await roomGrain.Whisper(words, myInfo);
            }
            return "You whispered '" + words + "'";
        }

        async Task<string> Shout(string words)
        {
            //TODO: Find a way to tell everyone in all rooms
            if (this.roomGrain != null)
            {
                await roomGrain.Shout(words, myInfo);
            }
            
            return "You shouted '" + words + "'";
        }

        async Task<string> Leave()
        {
            await DropAllItems();

            if (this.roomGrain != null)
            {
                await roomGrain.Leave(myInfo);
            }            

            return "You left the game";           
        }

        async Task<string> IPlayerGrain.Play(string command)
        {
            Thing thing;
            command = RemoveStopWords(command);

            string[] words = command.Split(' ');

            string verb = words[0].ToLower();

            if (killed && verb != "end")
                return await CheckAlive();


            verb = map_shortcuts(verb);

           
            switch (verb)
            {
                case "l":
                case "look":
                    return await this.roomGrain.Description(myInfo);

                case "go":
                    if (words.Length == 1)
                        return "Go where?";
                    return await Go(words[1]);

                case "north":
                case "south":
                case "east":
                case "west":
                    return await Go(verb);

                case "kill":
                    if (words.Length == 1)
                        return "Kill what?";
                    var target = command.Substring(verb.Length + 1);
                    return await Kill(target);

                case "drop":
                    thing = FindMyThing(Rest(words));
                    return await Drop(thing);

                case "take":
                    thing = await roomGrain.FindThing(Rest(words));
                    return await Take(thing);

                case "i":
                case "inv":
                case "inventory":
                    return "You are carrying: " + string.Join(" ", things.Select(x => x.Name));

                case "shout":
                    return await Shout(Rest(words));

                case "whisper":
                    return await Whisper(Rest(words));

                case "help":
                    return "Available commands: Look, North, South, East, West, Kill, Drop, Take, Inventory, Shout, Whisper, End";
                case "end":
                    await Leave();
                    return "";
            }
            return "I don't understand.";
        }

        private string map_shortcuts(string verb)
        {
            // Map shortcuts
            switch (verb)
            {
                case "n":
                    verb = "north";
                    break;
                case "s":
                    verb = "south";
                    break;
                case "e":
                    verb = "east";
                    break;
                case "w":
                    verb = "west";
                    break;
            }

            return verb;
        }
    }
}
