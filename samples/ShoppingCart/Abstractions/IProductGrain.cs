namespace Orleans.ShoppingCart.Abstractions;

public interface IProductGrain : IGrainWithStringKey
{
    Task<(bool IsAvailable, ProductDetails? ProductDetails)> TryTakeProductAsync(int quantity);

    Task ReturnProductAsync(int quantity);

    Task<int> GetProductAvailabilityAsync();

    Task CreateOrUpdateProductAsync(ProductDetails productDetails);

    Task<ProductDetails> GetProductDetailsAsync();
}
