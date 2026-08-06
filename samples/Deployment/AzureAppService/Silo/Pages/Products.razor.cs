// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

using Orleans.ShoppingCart.Silo.Components;

namespace Orleans.ShoppingCart.Silo.Pages;

public sealed partial class Products
{
    private HashSet<ProductDetails> _products = [];
    private ManageProductModal? _modal;

    [Inject]
    public InventoryService InventoryService { get; set; } = null!;

    [Inject]
    public ProductService ProductService { get; set; } = null!;

    [Inject]
    public AuthenticationStateProvider AuthenticationStateProvider { get; set; } = null!;

    protected override async Task OnInitializedAsync() =>
        _products = await InventoryService.GetAllProductsAsync();

    private async Task CreateNewProductAsync()
    {
        if (_modal is not null)
        {
            var generated = ProductDetailsExtensions.ProductDetailsFaker.Generate();
            var newProduct = new ProductDetails
            {
                Id = Guid.NewGuid().ToString("N"),
                ImageUrl = generated.ImageUrl,
                DetailsUrl = generated.DetailsUrl,
            };
            await _modal.OpenAsync("Create Product", CreateProductAsync, newProduct);
        }
    }

    private async Task EditProductAsync(ProductDetails product)
    {
        if (_modal is not null)
        {
            await _modal.OpenAsync("Edit Product", UpdateProductAsync, product);
        }
    }

    private Task CreateProductAsync(ProductDetails product) => SaveProductAsync(product);

    private Task UpdateProductAsync(ProductDetails product) => SaveProductAsync(product);

    private async Task SaveProductAsync(ProductDetails product)
    {
        var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        await ProductService.CreateOrUpdateProductAsync(authenticationState.User, product);
        _products = await InventoryService.GetAllProductsAsync();
        StateHasChanged();
    }
}
