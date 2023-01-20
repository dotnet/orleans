using eShop.Domain.Base;
using eShop.Domain.ShoppingCart.Entity;
using eShop.Domain.ShoppingCart.Requests;
using GrainsAbstraction;
using MediatR;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace Server.Grains;


public static class ApplicationStreamProviderGrainExtension
{
    public static IStreamProvider GetApplicationStreamProvider(this Grain grain)
    {
        return grain.GetStreamProvider("RabbitMQ");
    }
}

internal class ShoppingCartGrain : Grain, IShoppingCartGrain
{
    private readonly IMediator _mediator;
    private readonly IUnitOfWork _unitOfWork;
    private ShoppingCartEntity _shoppingCart = null!;
    private IAsyncStream<ShoppingCartResponse> _shoppingCartCreatedStream = null!;
    private IAsyncStream<(ShoppingCartResponse, ItemResponse)> _itemAddedToShoppingCart = null!;

    public ShoppingCartGrain(IMediator mediator, IUnitOfWork unitOfWork)
    {
        _mediator = mediator;
        _unitOfWork = unitOfWork;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetApplicationStreamProvider();
        _shoppingCartCreatedStream = streamProvider.GetStream<ShoppingCartResponse>(StreamId.Create("ShoppingCartCreated", this.GetPrimaryKey()));
        _itemAddedToShoppingCart = streamProvider.GetStream<(ShoppingCartResponse, ItemResponse)>(StreamId.Create("ItemAddedToCart", this.GetPrimaryKey()));

        var (isNew, cart) = await _mediator.Send(new GetCartRequest(this.GetPrimaryKey()));

        if (isNew)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            //await _shoppingCartCreatedStream.OnNextAsync(cart.ToCartResponse());
        }
            

        _shoppingCart = cart;
    }


    public Task<ShoppingCartResponse> Get() => Task.FromResult(_shoppingCart.ToCartResponse());

    public async Task<ShoppingCartResponse> AddItem(GrainCancellationToken grainCancellationToken, Guid itemId)
    {
        var (item, cart) = await _mediator.Send(new AddItemRequest(itemId, this.GetPrimaryKey()));
        await _unitOfWork.SaveChangesAsync(grainCancellationToken.CancellationToken);
        _shoppingCart = cart;
        await _itemAddedToShoppingCart.OnNextAsync((_shoppingCart.ToCartResponse(), item.ToItemResponse()));
        return _shoppingCart.ToCartResponse();
    }
}
