using System.Text;
using AdventureGrainInterfaces;
using Orleans;

namespace AdventureGrains;

public class PlayerGrain : Grain, IPlayerGrain
{
    private IRoomGrain? _roomGrain; // Current room
    private readonly List<Thing> _things = new(); // Things that the player is carrying

    private bool _killed = false;
    private PlayerInfo _myInfo = null!;

    public override Task OnActivateAsync()
    {
        _myInfo = new(this.GetPrimaryKey(), "nobody");
        return base.OnActivateAsync();
    }

    Task<string?> IPlayerGrain.Name() => Task.FromResult(_myInfo?.Name);

    Task<IRoomGrain> IPlayerGrain.RoomGrain() => Task.FromResult(_roomGrain!);


    async Task IPlayerGrain.Die()
    {
        // Drop everything
        var tasks = _things.Select(Drop).ToList();
        await Task.WhenAll(tasks);

        // Exit the game
        if (_roomGrain is not null && _myInfo is not null)
        {
            await _roomGrain.Exit(_myInfo);

            _roomGrain = null;
            _killed = true;
        }
    }

    private async Task<string?> Drop(Thing? thing)
    {
        if (_killed)
        {
            return await CheckAlive();
        }

        if (_roomGrain is not null && thing is not null)
        {
            _things.Remove(thing);
            await _roomGrain.Drop(thing);

            return "Okay.";
        }

        return "I don't understand.";
    }

    private async Task<string?> Take(Thing? thing)
    {
        if (_killed)
        {
            return await CheckAlive();
        }

        if (_roomGrain is not null && thing is not null)
        {
            _things.Add(thing);
            await _roomGrain.Take(thing);

            return "Okay.";
        }

        return "I don't understand.";
    }

    Task IPlayerGrain.SetName(string name)
    {
        _myInfo = _myInfo with { Name = name };
        return Task.CompletedTask;
    }

    Task IPlayerGrain.SetRoomGrain(IRoomGrain room)
    {
        _roomGrain = room;
        return room.Enter(_myInfo);
    }

    private async Task<string> Go(string direction)
    {
        IRoomGrain? destination = null;
        if (_roomGrain is not null)
        {
            destination = await _roomGrain.ExitTo(direction);
        }

        var description = new StringBuilder();

        if (_roomGrain is not null && destination is not null)
        {
            await _roomGrain.Exit(_myInfo);
            await destination.Enter(_myInfo);

            _roomGrain = destination;

            var desc = await destination.Description(_myInfo);
            if (desc != null)
            {
                description.Append(desc);
            }
        }
        else
        {
            description.Append("You cannot go in that direction.");
        }

        if (_things is { Count: > 0 })
        {
            description.AppendLine("You are holding the following items:");
            foreach (var thing in _things)
            {
                description.AppendLine(thing.Name);
            }
        }

        return description.ToString();
    }

    private async Task<string?> CheckAlive()
    {
        if (_killed is false)
        {
            return null;
        }

        // Go to room '-2', which is the place of no return.
        var room = GrainFactory.GetGrain<IRoomGrain>(-2);
        return await room.Description(_myInfo);
    }

    private async Task<string> Kill(string target)
    {
        if (_things.Count is 0)
        {
            return "With what? Your bare hands?";
        }

        if (_roomGrain is not null &&
            await _roomGrain.FindPlayer(target) is PlayerInfo player)
        {
            if (_things.Any(t => t.Category == "weapon"))
            {
                await GrainFactory.GetGrain<IPlayerGrain>(player.Key).Die();
                return $"{target} is now dead.";
            }

            return "With what? Your bare hands?";
        }

        if (_roomGrain is not null &&
            await _roomGrain.FindMonster(target) is MonsterInfo monster)
        {
            var weapons = monster.KilledBy?.Join(_things, id => id, t => t.Id, (id, t) => t);
            if (weapons?.Any() ?? false)
            {
                await GrainFactory.GetGrain<IMonsterGrain>(monster.Id).Kill(_roomGrain);
                return $"{target} is now dead.";
            }

            return "With what? Your bare hands?";
        }

        return $"I can't see {target} here. Are you sure?";
    }

    private string RemoveStopWords(string s)
    {
        var stopwords = new[] { " on ", " the ", " a " };

        StringBuilder builder = new(s);
        foreach (var word in stopwords)
        {
            builder.Replace(word, " ");
        }

        return builder.ToString();
    }

    private Thing? FindMyThing(string name) =>
        _things.FirstOrDefault(x => x.Name == name);

    private string Rest(string[] words)
    {
        StringBuilder builder = new();

        for (var i = 1; i < words.Length; ++i)
        {
            builder.Append($"{words[i]} ");
        }

        return builder.ToString().Trim().ToLower();
    }

    async Task<string?> IPlayerGrain.Play(string command)
    {
        command = RemoveStopWords(command);

        string[] words = command.Split(' ');
        string verb = words[0].ToLower();

        if (_killed && verb is not "end")
        {
            return await CheckAlive();
        }

        if (_roomGrain is null)
        {
            return "I don't understand.";
        }

        return verb switch
        {
            "look" =>
                await _roomGrain.Description(_myInfo),

            "go" => words.Length == 1
                ? "Go where?"
                : await Go(words[1]),

            "north" or "south" or "east" or "west" => await Go(verb),

            "kill" => words.Length == 1
                ? "Kill what?"
                : await Kill(command[(verb.Length + 1)..]),

            "drop" => await Drop(FindMyThing(Rest(words))),

            "take" => await Take(await _roomGrain.FindThing(Rest(words))),

            "inv" or "inventory" =>
                "You are carrying: " +
                $"{string.Join(" ", _things.Select(x => x.Name))}",

            "end" => "",

            _ => "I don't understand"
        };
    }
}
