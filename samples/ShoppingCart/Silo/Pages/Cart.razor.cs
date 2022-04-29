// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.Pages;

public sealed partial class Cart
{
    private HashSet<CartItem>? _cartItems;

    [Inject]
    public ShoppingCartService ShoppingCart { get; set; } = null!;

    [Inject]
    public ComponentStateChangedObserver Observer { get; set; } = null!;

    protected override Task OnInitializedAsync() => GetCartItemsAsync();

    private Task GetCartItemsAsync() =>
        InvokeAsync(async () =>
        {
            _cartItems = await ShoppingCart.GetAllItemsAsync();
            StateHasChanged();
        });

    private async Task OnItemRemovedAsync(ProductDetails product)
    {
        await ShoppingCart.RemoveItemAsync(product);
        await Observer.NotifyStateChangedAsync();

        _ = _cartItems?.RemoveWhere(item => item.Product == product);
    }

    private async Task OnItemUpdatedAsync((int Quantity, ProductDetails Product) tuple)
    {
        await ShoppingCart.AddOrUpdateItemAsync(tuple.Quantity, tuple.Product);
        await GetCartItemsAsync();
    }

    private async Task EmptyCartAsync()
    {
        await ShoppingCart.EmptyCartAsync();
        await Observer.NotifyStateChangedAsync();

        _cartItems?.Clear();
    }
}
