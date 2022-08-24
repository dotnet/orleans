using System.Reflection;
using ChatRoom;
using Orleans;
using Orleans.Hosting;
using Spectre.Console;

var client = new ClientBuilder()
    .UseLocalhostClustering()
    .ConfigureApplicationParts(
        parts => parts.AddApplicationPart(typeof(IChannelGrain).Assembly).WithReferences())
    .AddSimpleMessageStreamProvider("chat")
    .Build();

PrintUsage();

ClientContext context = new(client);
await StartAsync(context);
context = context with
{
    UserName = AnsiConsole.Ask<string>("What is your [aqua]name[/]?")
};
await ProcessLoopAsync(context);
await StopAsync(context);

static Task StartAsync(ClientContext context) =>
    AnsiConsole.Status().StartAsync("Connecting to server", async ctx =>
    {
        ctx.Spinner(Spinner.Known.Dots);
        ctx.Status = "Connecting...";

        await context.Client.Connect(async error =>
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

static async Task ProcessLoopAsync(ClientContext context)
{
    string? input = null;
    do
    {
        input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (input.StartsWith("/exit") &&
            AnsiConsole.Confirm("Do you really want to exit?"))
        {
            break;
        }

        var firstTwoCharacters = input[..2];
        if (firstTwoCharacters is "/n")
        {
            context = context with { UserName = input.Replace("/n", "").Trim() };
            AnsiConsole.MarkupLine(
                "[dim][[STATUS]][/] Set username to [lime]{0}[/]", context.UserName);
            continue;
        }

        if (firstTwoCharacters switch
            {
                "/j" => JoinChannel(context, input.Replace("/j", "").Trim()),
                "/l" => LeaveChannel(context),
                _ => null
            } is Task<ClientContext> cxtTask)
        {
            context = await cxtTask;
            continue;
        }

        if (firstTwoCharacters switch
            {
                "/h" => ShowCurrentChannelHistory(context),
                "/m" => ShowChannelMembers(context),
                _ => null
            } is Task task)
        {
            await task;
            continue;
        }

        await SendMessage(context, input);
    } while (input is not "/exit");
}

static Task StopAsync(ClientContext context) =>
    AnsiConsole.Status().StartAsync("Disconnecting...", async ctx =>
    {
        ctx.Spinner(Spinner.Known.Dots);
        await context.Client.Close();
    });

static void PrintUsage()
{
    AnsiConsole.WriteLine();
    using var logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ChatRoom.Client.logo.png");
    var logo = new CanvasImage(logoStream!)
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

    var rightTable = new Table()
        .HideHeaders()
        .Border(TableBorder.None)
        .AddColumn(new TableColumn("Content"));

    rightTable.AddRow(header)
        .AddRow(header2)
        .AddEmptyRow()
        .AddEmptyRow()
        .AddRow(markup);
    table.AddRow(logo, rightTable);

    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();
}

static async Task ShowChannelMembers(ClientContext context)
{
    var room = context.Client.GetGrain<IChannelGrain>(context.CurrentChannel);
    var members = await room.GetMembers();

    AnsiConsole.Write(new Rule($"Members for '{context.CurrentChannel}'")
    {
        Alignment = Justify.Center,
        Style = Style.Parse("darkgreen")
    });

    foreach (var member in members)
    {
        AnsiConsole.MarkupLine("[bold yellow]{0}[/]", member);
    }

    AnsiConsole.Write(new Rule()
    {
        Alignment = Justify.Center,
        Style = Style.Parse("darkgreen")
    });
}

static async Task ShowCurrentChannelHistory(ClientContext context)
{
    var room = context.Client.GetGrain<IChannelGrain>(context.CurrentChannel);
    var history = await room.ReadHistory(1_000);

    AnsiConsole.Write(new Rule($"History for '{context.CurrentChannel}'")
    {
        Alignment = Justify.Center,
        Style = Style.Parse("darkgreen")
    });

    foreach (var chatMsg in history)
    {
        AnsiConsole.MarkupLine("[[[dim]{0}[/]]] [bold yellow]{1}:[/] {2}",
            chatMsg.Created.LocalDateTime, chatMsg.Author, chatMsg.Text);
    }

    AnsiConsole.Write(new Rule()
    {
        Alignment = Justify.Center,
        Style = Style.Parse("darkgreen")
    });
}

static async Task SendMessage(
    ClientContext context,
    string messageText)
{
    var room = context.Client.GetGrain<IChannelGrain>(context.CurrentChannel);
    await room.Message(new ChatMsg(context.UserName, messageText));
}

static async Task<ClientContext> JoinChannel(
    ClientContext context,
    string channelName)
{
    if (context.CurrentChannel is not null &&
        !string.Equals(context.CurrentChannel, channelName, StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine(
            "[bold olive]Leaving channel [/]{0}[bold olive] before joining [/]{1}",
            context.CurrentChannel, channelName);

        await LeaveChannel(context);
    }

    AnsiConsole.MarkupLine("[bold aqua]Joining channel [/]{0}", channelName);
    context = context with { CurrentChannel = channelName };
    await AnsiConsole.Status().StartAsync("Joining channel...", async ctx =>
    {
        var room = context.Client.GetGrain<IChannelGrain>(context.CurrentChannel);
        var streamId = await room.Join(context.UserName!);
        var stream =
            context.Client
                .GetStreamProvider("chat")
                .GetStream<ChatMsg>(streamId, "default");

        //subscribe to the stream to receive furthur messages sent to the chatroom
        await stream.SubscribeAsync(new StreamObserver(channelName));
    });
    AnsiConsole.MarkupLine("[bold aqua]Joined channel [/]{0}", context.CurrentChannel!);
    return context;
}

static async Task<ClientContext> LeaveChannel(ClientContext context)
{
    AnsiConsole.MarkupLine(
        "[bold olive]Leaving channel [/]{0}",
        context.CurrentChannel!);

    await AnsiConsole.Status().StartAsync("Leaving channel...", async ctx =>
    {
        var room = context.Client.GetGrain<IChannelGrain>(context.CurrentChannel);
        var streamId = await room.Leave(context.UserName!);
        var stream =
            context.Client
                .GetStreamProvider("chat")
                .GetStream<ChatMsg>(streamId, "default");

        //unsubscribe from the channel/stream since client left, so that client won't
        //receive future messages from this channel/stream
        var subscriptionHandles = await stream.GetAllSubscriptionHandles();
        foreach (var handle in subscriptionHandles)
        {
            await handle.UnsubscribeAsync();
        }
    });

    AnsiConsole.MarkupLine("[bold olive]Left channel [/]{0}", context.CurrentChannel!);
    return context with { CurrentChannel = null };
}
