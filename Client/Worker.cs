using GrainsAbstraction;
using Orleans;

namespace Client;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IClusterClient _clusterClient;

    public Worker(ILogger<Worker> logger, IClusterClient clusterClient)
    {
        _logger = logger;
        _clusterClient = clusterClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(3000);
        var token = new GrainCancellationTokenSource();
        
        var carts = Enumerable.Range(1, 5000).Select(_ => _clusterClient.GetGrain<IShoppingCartGrain>(Guid.NewGuid())).ToArray();

        var addArthasActionFiguresActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 100).Select(_ => Guid.Parse("fd5036cf-e2b3-494f-a13b-8b75089d4eb6")).ToList());

        var addLaptopsActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 2000).Select(_ => Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372")).ToList());

        var addRazerBladeActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 1000).Select(_ => Guid.Parse("550bf867-1c54-4a94-86be-181fcf077161")).ToList());

        var addStandingDesksActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 100).Select(_ => Guid.Parse("d04191ca-a5b3-4e4b-a941-436c38248d14")).ToList());

        var addAkkoKeyboardsActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 200).Select(_ => Guid.Parse("18662f45-8d99-48c7-8a1e-70d363135608")).ToList());

        var addRazerTiamatActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 400).Select(_ => Guid.Parse("6d4c8f2c-dac4-40fd-83c0-966f270727bf")).ToList());

        var addNintendoSwitchsActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 1000).Select(_ => Guid.Parse("0d8f0aa5-3498-43ba-8cce-976034e65895")).ToList());

        var addiPhonesActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 5000).Select(_ => Guid.Parse("6269cefb-dd34-41b5-8b55-a5586acb07d1")).ToList());

        var addPendrivesActions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 1000).Select(_ => Guid.Parse("e4828d47-499f-4dcf-a575-c61f89a89250")).ToList());

        var addPlaystation5Actions = AddItemsToCarts(carts, token.Token,
            Enumerable.Range(1, 800).Select(_ => Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")).ToList());


        //var actions = addArthasActionFiguresActions
        //    .Concat(addLaptopsActions)
        //    .Concat(addRazerBladeActions)
        //.Concat(addStandingDesksActions)
        //.Concat(addPlaystation5Actions)
        //.Concat(addAkkoKeyboardsActions)
        //.Concat(addRazerTiamatActions)
        //.Concat(addNintendoSwitchsActions)
        //.Concat(addiPhonesActions)
        //.Concat(addPendrivesActions).ToList();

        //await Task.WhenAll(actions.Select(e => e()));

        //var hasMore = true;
        //var amountToTake = 3000;
        //var lastSkipped = 0;
        //while (hasMore)
        //{
        //    var actionsToPublish = actions.Skip(lastSkipped).Take(amountToTake).ToList();
        //    lastSkipped += amountToTake;
        //    await Task.WhenAll(actionsToPublish.Select(e => e()));
        //    Console.ReadLine();
        //    hasMore = actionsToPublish.Count > 0;
        //}

        //Console.WriteLine("no more messages");


        for (int i = 0; i < 1; i++)
        {
            var cartId = Guid.NewGuid();
            await _clusterClient.GetGrain<IShoppingCartGrain>(cartId)
                .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396"));
            //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId)
            //    .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396"));
            //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId)
            //    .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396"));
        }


        //while (true)
        //{
        //    var cartId = Guid.NewGuid();
        //    for (int i = 0; i < 100; i++)
        //    {
        //        await Task.WhenAll(_clusterClient.GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")), _clusterClient
        //            .GetGrain<IShoppingCartGrain>(cartId)
        //            .AddItem(token.Token, Guid.Parse("56e98e59-076a-41ac-b4c3-cefbc3806396")));
        //}
        //    Console.WriteLine("Press any key to send a message");
        //    Console.ReadLine();
        //}

        //await Task.WhenAll(actions.Select(a => a()));

        //Console.ReadLine();


    }

    private List<Func<Task>> AddItemsToCarts(IShoppingCartGrain[] shoppingCarts, GrainCancellationToken cancellationToken, List<Guid> itemsIds)
    {
        Random rng = new Random();
        var actions = new List<Func<Task>>();

        while (itemsIds.Count > 0)
        {
            var itemsTaken = itemsIds.Take(1);
            //Random amount of items
            foreach (var itemId in itemsTaken)
            {
                //Add to random cart
                actions.Add(() => shoppingCarts[rng.Next(0, shoppingCarts.Length)].AddItem(cancellationToken, itemId));
            }
            itemsIds.RemoveRange(0, itemsTaken.Count());
        }

        return actions;
    }
}
