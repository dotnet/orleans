using eShop.Domain.Base;
using eShop.Domain.Stock.Requests;
using GrainsAbstraction;
using MediatR;

namespace Server.Grains;

public class StockGrain : Grain, IStockGrain
{
    private readonly IMediator _mediator;
    private readonly IUnitOfWork _unitOfWork;

    public StockGrain(IMediator mediator, IUnitOfWork unitOfWork)
    {
        _mediator = mediator;
        _unitOfWork = unitOfWork;
    }

    public async Task MoveStockItem(GrainCancellationToken grainCancellationToken, Guid itemId, int quantity)
    {
        await _mediator.Send(new MoveStockItemRequest(itemId, quantity), grainCancellationToken.CancellationToken);
        await _unitOfWork.SaveChangesAsync(grainCancellationToken.CancellationToken);
    }
}