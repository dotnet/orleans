// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

using Orleans.ShoppingCart.Silo.Components;

namespace Orleans.ShoppingCart.Silo.Pages;

public sealed partial class Products
{
    private HashSet<ProductDetails>? _products;
    private ManageProductModal? _modal;

    [Parameter]
    public string? Id { get; set; }

    [Inject]
    public InventoryService InventoryService { get; set; } = null!;

    [Inject]
    public ProductService ProductService { get; set; } = null!;

    [Inject]
    public IDialogService DialogService  { get; set; } = null!;

    protected override async Task OnInitializedAsync() =>
        _products = await InventoryService.GetAllProductsAsync();

    private async Task CreateNewProductAsync()
    {
        if (_modal is not null)
        {
            await _modal.OpenAsync("Create Product", OnProductUpdated);
        }
    }

    private async Task OnProductUpdated(ProductDetails product)
    {
        var fake = ProductDetailsExtensions.ProductDetailsFaker.Generate();
        product = product with
        {
            Id = fake.Id,
            ImageUrl = fake.ImageUrl,
            DetailsUrl = fake.DetailsUrl
        };
        await ProductService.CreateOrUpdateProductAsync(product);
        _products = await InventoryService.GetAllProductsAsync();

        _modal?.Close();

        StateHasChanged();
    }

    private Task OnEditProduct(ProductDetails product) =>
        product is not null ? OnProductUpdated(product) : Task.CompletedTask;
}
