using eShop.Domain.Base;
using eShop.Domain.Stock;
using GrainsAbstraction;
using MediatR;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Server.Grains;

[ImplicitStreamSubscription("ItemAddedToCart")]
[ImplicitStreamSubscription("ShoppingCartCreated")]
public class StockCartListenerLog : Grain, IGrainWithGuidKey
{
    private readonly ILogger<StockCartListenerLog> _logger;
    private readonly IOffsetRepository _offsetRepository;
    private IAsyncStream<(ShoppingCartResponse, ItemResponse)> _itemAddedToCartStream = null!;
    private IServiceScope _scope = null!;

    public StockCartListenerLog(ILogger<StockCartListenerLog> logger, IOffsetRepository offsetRepository)
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
            var lastOffsetFromDatabase = await _offsetRepository.Find(this.GetPrimaryKey(), cancellationToken, false);

            var lastToken = lastOffsetFromDatabase?.LastTokenRead;
            var token = lastToken.HasValue ? new EventSequenceTokenV2(lastToken.Value) : null;
            await _itemAddedToCartStream.SubscribeAsync(
                new StockCartGrainObserver(_scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                    _scope.ServiceProvider.GetRequiredService<IOffsetRepository>(),
                    _scope.ServiceProvider.GetRequiredService<ILogger<StockCartGrainObserver>>(),
                    _scope.ServiceProvider.GetRequiredService<IMediator>(), this.GetPrimaryKey()), token);
        }, AsyncExecutorWithRetries.INFINITE_RETRIES, (_, _) => !cancellationToken.IsCancellationRequested, TimeSpan.MaxValue, new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)));


    }
}