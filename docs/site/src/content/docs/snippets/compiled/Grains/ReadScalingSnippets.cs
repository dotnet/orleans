using Orleans;
using Orleans.Concurrency;

namespace Documentation.Grains.ReadScaling;

[GenerateSerializer]
public sealed record ProductSnapshot(
    [property: Id(0)] string Name,
    [property: Id(1)] decimal Price,
    [property: Id(2)] long Version);

[GenerateSerializer]
public sealed record ProductView(
    [property: Id(0)] ProductSnapshot Product,
    [property: Id(1)] string Recommendation);

// <single_writer_interleaved_readers>
public interface IProductRecommendationService
{
    ValueTask<string> GetRecommendation(string productId);
}

public interface IProductGrain : IGrainWithStringKey
{
    [ReadOnly]
    ValueTask<ProductView> Get();

    ValueTask Update(string name, decimal price);
}

public sealed class ProductGrain(
    IProductRecommendationService recommendations)
    : Grain, IProductGrain
{
    private ProductSnapshot _snapshot =
        new("Product", 0m, Version: 0);

    public async ValueTask<ProductView> Get()
    {
        ProductSnapshot snapshot = _snapshot;
        string recommendation = await recommendations.GetRecommendation(
            this.GetPrimaryKeyString());

        return new ProductView(snapshot, recommendation);
    }

    public ValueTask Update(string name, decimal price)
    {
        _snapshot = new ProductSnapshot(
            name,
            price,
            _snapshot.Version + 1);

        return ValueTask.CompletedTask;
    }
}
// </single_writer_interleaved_readers>
