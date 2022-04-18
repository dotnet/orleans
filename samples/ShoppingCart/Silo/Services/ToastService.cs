namespace Orleans.ShoppingCart.Silo.Services;

public sealed class ToastService
{
    public event Func<(string Title, string Message), Task>? OnToastedRequested;

    public async Task ShowToastAsync(string title, string message)
    {
        if (OnToastedRequested is not null)
        {
            await OnToastedRequested.Invoke((title, message));
        }
    }
}
