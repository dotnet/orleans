using System.Linq;
using GrainsAbstraction;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Orleans;
using Orleans.Configuration;
using Orleans.GrainReferences;

namespace Server;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IClusterClient _clusterClient;

    public Worker(ILogger<Worker> logger, IClusterClient clusterClient, IOptions<ClusterOptions> clusterOptions)
    {
        _logger = logger;
        _clusterClient = clusterClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(10000);
        var token = new GrainCancellationTokenSource();
        //var counter = 0;
        var requests = new List<Task>();
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


        var actions = addArthasActionFiguresActions
            .Concat(addLaptopsActions)
            .Concat(addRazerBladeActions)
            .Concat(addStandingDesksActions)
            .Concat(addPlaystation5Actions)
            .Concat(addAkkoKeyboardsActions)
            .Concat(addRazerTiamatActions)
            .Concat(addNintendoSwitchsActions)
            .Concat(addiPhonesActions)
            .Concat(addPendrivesActions);

        //var actions = addLaptopsActions;

        //await Task.WhenAll(actions.Select(a => a()));

        var cartId = Guid.Parse("920f076e-6a3f-4df0-ace0-b17958ab3555");
        //var cartId2 = Guid.NewGuid();
        //var cartId3 = Guid.NewGuid();
        ////_logger.LogInformation($"cartId: {cartId}");
        ////_logger.LogInformation($"cartId2: {cartId2}");
        ////_logger.LogInformation($"cartId3: {cartId3}");
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372")); // => 0
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId2).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 1
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 2
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 3
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 4
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId3).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 5
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 6
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId2).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 7
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId3).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 8
        //await _clusterClient.GetGrain<IShoppingCartGrain>(cartId).AddItem(token.Token, Guid.Parse("49badfd4-d959-4efd-a944-023530ca0372"));// => 9

        //await Task.Delay(20000);

        //await Task.WhenAll(requests);

        //_logger.LogInformation("I'm done");
        Console.ReadLine();
    }

    private List<Func<Task>> AddItemsToCarts(IShoppingCartGrain[] shoppingCarts, GrainCancellationToken cancellationToken, List<Guid> itemsIds)
    {
        Random rng = new Random();
        var actions = new List<Func<Task>>();
        
        while (itemsIds.Count > 0)
        {
            var itemsTaken = itemsIds.Take(rng.Next(1 ,4));
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
