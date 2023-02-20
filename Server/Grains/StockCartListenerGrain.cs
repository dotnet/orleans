using eShop.Domain.Base;
using eShop.Domain.ShoppingCart.Requests;
using eShop.Domain.Stock;
using eShop.Domain.Stock.Requests;
using GrainsAbstraction;
using MediatR;
using Microsoft.VisualBasic;
using Orleans;
using Orleans.Internal;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Server.Grains;


public class StockCartGrainObserver : IAsyncObserver<(ShoppingCartResponse, ItemResponse)>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOffsetRepository _offsetRepository;
    private readonly ILogger<StockCartGrainObserver> _logger;
    private readonly IMediator _mediator;
    private readonly Guid _id;
    private readonly string _offsetName;
    private (ShoppingCartResponse, ItemResponse) _lastMesage;
    private bool _lastTimeFailed = false;
    private OffsetRead? _lastOffset = null;

    public StockCartGrainObserver(IUnitOfWork unitOfWork, IOffsetRepository offsetRepository, ILogger<StockCartGrainObserver> logger, IMediator mediator, Guid id, string offsetName)
    {
        _unitOfWork = unitOfWork;
        _offsetRepository = offsetRepository;
        _logger = logger;
        _mediator = mediator;
        _id = id;
        _offsetName = offsetName;
    }

    public async Task OnNextAsync((ShoppingCartResponse, ItemResponse) message, StreamSequenceToken? token = null)
    {
        try
        {
            _lastOffset = await _offsetRepository.FindByOffsetNameAndId(_offsetName, _id, default);
            _lastMesage = message;

            if (_lastTimeFailed && _lastOffset?.LastTokenRead == token!.SequenceNumber)
                return;
            
            //if (_lastOffset is { Name: nameof(StockCartListenerLog) })
            //{
            //    throw new Exception("This consumer is failing");
            //}


            _lastTimeFailed = false;
            if (_lastOffset is null)
            {
                _lastOffset = new OffsetRead { LastTokenRead = token?.SequenceNumber, Id = _id, Name = _offsetName };
                _offsetRepository.Add(_lastOffset);
            }

            var (_, newShoppingCartItem) = message;
            await _mediator.Send(new MoveStockItemRequest(newShoppingCartItem.Id, -1));
            _lastOffset.LastTokenRead = token!.SequenceNumber;
            await _unitOfWork.SaveChangesAsync(default);

        }
        catch
        {
            _lastTimeFailed = true;
            _unitOfWork.ClearChanges();
            throw;
        }


    }

    public Task OnCompletedAsync()
    {
        _logger.LogInformation("Stream completed");
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        _logger.LogError(ex, "An error happened while trying to update the stock.");
        return Task.CompletedTask;
    }
}

[ImplicitStreamSubscription("ItemAddedToCart")]
[ImplicitStreamSubscription("ShoppingCartCreated")]
public class StockCartListenerGrain : Grain, IGrainWithGuidKey
{
    private readonly ILogger<StockCartListenerGrain> _logger;
    private readonly IOffsetRepository _offsetRepository;
    private IAsyncStream<(ShoppingCartResponse, ItemResponse)> _itemAddedToCartStream = null!;
    private IServiceScope _scope = null!;

    public StockCartListenerGrain(ILogger<StockCartListenerGrain> logger, IOffsetRepository offsetRepository)
    {
        _logger = logger;
        _offsetRepository = offsetRepository;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deactivating grain");
        _scope.Dispose();
        return Task.CompletedTask;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetApplicationStreamProvider();
        _itemAddedToCartStream = streamProvider.GetStream<(ShoppingCartResponse, ItemResponse)>(StreamId.Create("ItemAddedToCart", this.GetPrimaryKey()));
        _scope = ServiceProvider.CreateScope();

        await AsyncExecutorWithRetries.ExecuteWithRetries(async _ =>
        {
            var lastOffsetFromDatabase = await _offsetRepository.FindByOffsetNameAndId(nameof(StockCartListenerGrain), this.GetPrimaryKey(), cancellationToken, false);

            var lastToken = lastOffsetFromDatabase?.LastTokenRead;
            var token = lastToken.HasValue ? new EventSequenceTokenV2(lastToken.Value) : null;
            await _itemAddedToCartStream.SubscribeAsync(
                new StockCartGrainObserver(_scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                    _scope.ServiceProvider.GetRequiredService<IOffsetRepository>(),
                    _scope.ServiceProvider.GetRequiredService<ILogger<StockCartGrainObserver>>(),
                    _scope.ServiceProvider.GetRequiredService<IMediator>(), this.GetPrimaryKey(), nameof(StockCartListenerGrain)), token);
        }, AsyncExecutorWithRetries.INFINITE_RETRIES, (_, _) => !cancellationToken.IsCancellationRequested, TimeSpan.MaxValue, new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)));


    }
}