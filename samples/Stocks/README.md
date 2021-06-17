# Stocks

This application fetches stock prices from a remote service using `HttpClient`, caches them in a grain, and displays them on screen.

### Demonstrates

* How to use Orleans from within a [`BackgroundService`](https://docs.microsoft.com/aspnet/core/fundamentals/host/hosted-services#backgroundservice-base-class).
* How to use timers within a grain
* How to make external service calls using .NET's `HttpClient` and cache the results within a grain.

A [`BackgroundService`](https://docs.microsoft.com/aspnet/core/fundamentals/host/hosted-services#backgroundservice-base-class) periodically requests the stock price for a variety of stocks from corresponding `StockGrain` instances.
Each `StockGrain` is identified by its stock ticker symbol, for example, the string `"MSFT"`.

For the sample to display all of the stocks included, it requires replacing the `ApiKey` constant in `StockGrain.cs` with an API key obtained from https://www.alphavantage.co/support/#api-key.
The sample can be run without replacing this key, but a warning message may be printed with directions on how to obtain an API key.

To run the sample, execute `dotnet run`.
