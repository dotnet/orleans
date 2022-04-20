using Orleans.ShoppingCart.Silo.Components;

namespace Orleans.ShoppingCart.Silo.Pages;

public sealed partial class Products
{
    HashSet<ProductDetails>? _products;
    ManageProductModal? _modal;

    [Parameter]
    public string? Id { get; set; }

    [Inject]
    public InventoryService InventoryService { get; set; } = null!;

    [Inject]
    public ProductService ProductService { get; set; } = null!;

    protected override async Task OnInitializedAsync() =>
        _products = await InventoryService.GetAllProductsAsync();

    void CreateNewProduct()
    {
        if (_modal is not null)
        {
            var product = new ProductDetails();
            var faker = product.GetBogusFaker();
            var fake = faker.Generate();
            _modal.Product = product with
            {
                Id = fake.Id,
                ImageUrl = fake.ImageUrl,
                DetailsUrl = fake.DetailsUrl
            };
            _modal.Open();
        }
    }

    async Task OnProductUpdated(ProductDetails product)
    {
        await ProductService.CreateOrUpdateProductAsync(product);
        _products = await InventoryService.GetAllProductsAsync();

        _modal?.Close();

        StateHasChanged();
    }

    Task OnEditProduct(ProductDetails product)
    {
        if (_modal is not null)
        {
            _modal.Product = product;
            _modal.Open();
        }

        return Task.CompletedTask;
    }
}
