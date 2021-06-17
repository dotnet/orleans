# Adventure - Text Adventure Game

Before there were graphical user interfaces, before the era of game consoles and massive-multiplayer games, there were VT100 terminals and there was [Colossal Cave Adventure](https://en.wikipedia.org/wiki/Colossal_Cave_Adventure), [Zork](https://en.wikipedia.org/wiki/Zork), and [Microsoft Adventure](https://en.wikipedia.org/wiki/Microsoft_Adventure).
Possibly lame by today's standards, back then it was a magical world of monsters, chirping birds, and things you could pick up.
It's the inspiration for this sample.

<p align="center">
    <img src="./assets/BoxArt.jpg" />
</p>

### Demonstrates

* How to structure an application (in this case, a game) using grains
* How to connect an external client to an Orleans cluster (`ClientBuilder`)

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