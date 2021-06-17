# TicTacToe - Web-based Game

<p align="center">
    <img src="logo.png"/>
</p>

This sample demonstrates a Web-based [Tic-tac-toe](https://en.wikipedia.org/wiki/Tic-tac-toe) game.

The game is implemented as a single project, `TicTacToe.csproj`, which uses the [.NET Generic Host](https://docs.microsoft.com/dotnet/core/extensions/generic-host) to host an ASP.NET Core MVC application alongside Orleans.

The client side of the game is a JavaScript application which polls the MVC application for updates. MVC controllers forward requests on to grains. The application has 3 types of grains:

* `PlayerGrain` which represents a player, allowing the caller to join and leave games, update properties, and retrieve an overview of the past and present games.
* `GameGrain` which represents an individual game session and the accompanying game logic. `GameGrain` allows clients to make moves and see current game board state.
* `PairingGrain` which holds a list of the currently available games which other players can join.

The call flow is as follows:

![A diagram showing the calls made in the application](dataflow.png)

## Running the sample

Open a terminal window and execute the following at the command prompt:

``` bash
dotnet run 
```

The game server will start and you can open a browser to `http://localhost:5000/` to interact with the game.

If you wish, you can start more instances of the host to see them form a cluster. If you do so, add the `InstanceId` option on the command line to differentiate them. A production application would use something other than the "localhost clustering" which this application uses (see `Program.cs` for where clustering is configured via `UseLocalhostClustering`) and therefore this `InstanceId` option would not be necessary.

``` bash
dotnet run -- --InstanceId 1
```

Since the game uses cookies to identify players, you will need a separate browser session in order to be able to play against yourself and experience the game.