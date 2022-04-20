namespace Orleans.ShoppingCart.Silo.Services;

public sealed class ComponentStateChangedObserver
{
    public event Func<Task>? OnStateChanged;

    public Task NotifyStateChangedAsync() =>
        OnStateChanged?.Invoke() ?? Task.CompletedTask;
}
