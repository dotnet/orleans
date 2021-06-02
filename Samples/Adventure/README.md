# Adventure - text adventure game

This sample demonstrates a short text adventure game built using Orleans in the style of [Colossal Cave Adventure](https://en.wikipedia.org/wiki/Colossal_Cave_Adventure), [Zork](https://en.wikipedia.org/wiki/Zork), and [Microsoft Adventure](https://en.wikipedia.org/wiki/Microsoft_Adventure):

![Microsoft Adventure Box Art](./assets/BoxArt.jpg)

The system consists of two parts: a server executable called *AdventureServer* and a client executable called *AdventureClient*.
The server reads a game data file, `AdventureMap.json` by default, and initializes `RoomGrain` instances with that game data.
The client connects to the server and interacts with the game using the `IPlayerGrain` interface.
On the server, `IPlayerGrain` is implemented by `PlayerGrain`, so any calls to `IPlayerGrain` are routed to the corresponding `PlayerGrain` instance.
Clients issue commands to the game by calling `IPlayerGrain.Play(command)`, where `command` is a string entered by the player at the command prompt. `PlayerGrain` interprets each command and executes it, possibly issuing calls to a `RoomGrain` to interact with the room.

This is a simple game and there are only a few verbs which the game understands:

* `look` - to examine the current room
* `go <direction>` - to move to a different room.
* `north`, `south`, `east`, `west` - shortcuts for `go north`, etc
* `kill <target>` - kill a target
* `drop <thing>` - drop something from the player's inventory
* `take <thing>` - add an item from the current room to the player's inventory
* `inv` or `inventory` - examine the player's inventory
* `end` - exits the game

To run the game, first run the server by executing the following at the command prompt (opened to the base directory of the sample):

``` bash
dotnet run --project AdventureServer
```

You should see the server start up and eventually print the line `Press any key to exit`.

In a separate terminal, execute the following to start the client and play the game:

``` bash
dotnet run --project AdventureClient
```