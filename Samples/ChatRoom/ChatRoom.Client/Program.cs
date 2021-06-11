using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ChatRoom;
using Orleans;
using Orleans.Hosting;
using Spectre.Console;

//To make this sample simple
//In this sample, one client can only join one channel, hence we have a static variable of one channel name.
//client can send messages to the channel , and receive messages sent to the channel/stream from other clients. 
var client = new ClientBuilder()
    .UseLocalhostClustering()
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IChannelGrain).Assembly).WithReferences())
    .AddSimpleMessageStreamProvider("chat")
    .Build();

PrintUsage();

await AnsiConsole.Status().StartAsync("Connecting to server", async ctx =>
{
    ctx.Spinner(Spinner.Known.Dots);
    ctx.Status = "Connecting...";

    await client.Connect(async error =>
    {
        AnsiConsole.MarkupLine("[bold red]Error:[/] error connecting to server!");
        AnsiConsole.WriteException(error);
        ctx.Status = "Waiting to retry...";
        await Task.Delay(TimeSpan.FromSeconds(2));
        ctx.Status = "Retrying connection...";
        return true;
    });

    ctx.Status = "Connected!";
});

string currentChannel = null;
var userName = AnsiConsole.Ask<string>("What is your [aqua]name[/]?");

string input = null;
do
{
    input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input)) continue;

    if (input.StartsWith("/j"))
    {
        await JoinChannel(input.Replace("/j", "").Trim());
    }
    else if (input.StartsWith("/n"))
    {
        userName = input.Replace("/n", "").Trim();
        AnsiConsole.MarkupLine("[dim][[STATUS]][/] Set username to [lime]{0}[/]", userName);
    }
    else if (input.StartsWith("/l"))
    {
        await LeaveChannel();
    }
    else if (input.StartsWith("/h"))
    {
        await ShowCurrentChannelHistory();
    }
    else if (input.StartsWith("/m"))
    {
        await ShowChannelMembers();
    }
    else if (!input.StartsWith("/exit"))
    {
        await SendMessage(input);
    }
    else
    {
        if (AnsiConsole.Confirm("Do you really want to exit?"))
        {
            break;
        }
    }
} while (input != "/exit");

await AnsiConsole.Status().StartAsync("Disconnecting...", async ctx =>
{
    ctx.Spinner(Spinner.Known.Dots);
    await client.Close();
});

void PrintUsage()
{
    AnsiConsole.WriteLine();
    using var logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ChatRoom.Client.logo.png");
    var logo = new CanvasImage(logoStream)
    {
        MaxWidth = 25
    };

    var table = new Table()
    {
        Border = TableBorder.None,
        Expand = true, 
    }.HideHeaders();
    table.AddColumn(new TableColumn("One"));

    var header = new FigletText("Orleans")
    {
        Color = Color.Fuchsia
    };
    var header2 = new FigletText("Chat Room")
    {
        Color = Color.Aqua
    };

    var markup = new Markup(
       "[bold fuchsia]/j[/] [aqua]<channel>[/] to [underline green]join[/] a specific channel\n"
       + "[bold fuchsia]/n[/] [aqua]<username>[/] to set your [underline green]name[/]\n"
       + "[bold fuchsia]/l[/] to [underline green]leave[/] the current channel\n"
       + "[bold fuchsia]/h[/] to re-read channel [underline green]history[/]\n"
       + "[bold fuchsia]/m[/] to query [underline green]members[/] in the channel\n"
       + "[bold fuchsia]/exit[/] to exit\n"
       + "[bold aqua]<message>[/] to send a [underline green]message[/]\n");
    table.AddColumn(new TableColumn("Two"));
    var rightTable = new Table().HideHeaders().Border(TableBorder.None).AddColumn(new TableColumn("Content"));
    rightTable.AddRow(header).AddRow(header2).AddEmptyRow().AddEmptyRow().AddRow(markup);
    table.AddRow(logo, rightTable);

    AnsiConsole.Render(table);
    AnsiConsole.WriteLine();
}

async Task ShowChannelMembers()
{
    var room = client.GetGrain<IChannelGrain>(currentChannel);
    var members = await room.GetMembers();

    AnsiConsole.Render(new Rule($"Members for '{currentChannel}'")
    {
        Alignment = Justify.Center,
        Style = Style.Parse("darkgreen")
    });

    foreach (var member in members)
    {
        AnsiConsole.MarkupLine("[bold yellow]{0}[/]", member);
    }

    AnsiConsole.Render(new Rule()
    {
        Alignment = Justify.Center,
        Style = Style.Parse("darkgreen")
    });
}

async Task ShowCurrentChannelHistory()
{
    var room = client.GetGrain<IChannelGrain>(currentChannel);
    var history = await room.ReadHistory(1000);

    AnsiConsole.Render(new Rule($"History for '{currentChannel}'")
    {
        Alignment = Justify.Center,
        Style = Style.Parse("darkgreen")
    });

    foreach (var chatMsg in history)
    {
        AnsiConsole.MarkupLine("[[[dim]{0}[/]]] [bold yellow]{1}:[/] {2}", chatMsg.Created.LocalDateTime, chatMsg.Author, chatMsg.Text);
    }

    AnsiConsole.Render(new Rule()
    {
        Alignment = Justify.Center,
        Style = Style.Parse("darkgreen")
    });
}

async Task SendMessage(string messageText)
{
    var room = client.GetGrain<IChannelGrain>(currentChannel);
    await room.Message(new ChatMsg(userName, messageText));
}

async Task JoinChannel(string channelName)
{
    if (currentChannel is not null && !string.Equals(currentChannel, channelName, StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[bold olive]Leaving channel [/]{0}[bold olive] before joining [/]{1}", currentChannel, channelName);
        await LeaveChannel();
    }

    AnsiConsole.MarkupLine("[bold aqua]Joining channel [/]{0}", channelName);
    currentChannel = channelName;
    await AnsiConsole.Status().StartAsync("Joining channel...", async ctx =>
    {
        var room = client.GetGrain<IChannelGrain>(currentChannel);
        var streamId = await room.Join(userName);
        var stream = client.GetStreamProvider("chat").GetStream<ChatMsg>(streamId, "default");
        //subscribe to the stream to receiver furthur messages sent to the chatroom
        await stream.SubscribeAsync(new StreamObserver(channelName));
    });
    AnsiConsole.MarkupLine("[bold aqua]Joined channel [/]{0}", currentChannel);
}

async Task LeaveChannel()
{
    AnsiConsole.MarkupLine("[bold olive]Leaving channel [/]{0}", currentChannel);
    await AnsiConsole.Status().StartAsync("Leaving channel...", async ctx =>
    {
        var room = client.GetGrain<IChannelGrain>(currentChannel);
        var streamId = await room.Leave(userName);
        var stream = client.GetStreamProvider("chat").GetStream<ChatMsg>(streamId, "default");

        //unsubscribe from the channel/stream since client left, so that client won't
        //receive furture messages from this channel/stream
        var subscriptionHandles = await stream.GetAllSubscriptionHandles();
        foreach (var handle in subscriptionHandles)
        {
            await handle.UnsubscribeAsync();
        }
    });

    AnsiConsole.MarkupLine("[bold olive]Left channel [/]{0}", currentChannel);
}
