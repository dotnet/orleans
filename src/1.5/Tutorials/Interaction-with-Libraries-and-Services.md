---
layout: page
title: Interaction with Libraries and Services
---

# Interaction with Libraries and Services

Code running in a grain is not prohibited from calling external systems or services, but the rule for always using asynchronous code must be maintained.

In this sample we'll see how a grain can call out to an external service.

## Creating a Stock Grain

For this sample, let's create a grain which maintains the current price for a stock.

Create a grain interface project, and add an interface for an `IStockGrain`:

``` csharp
public interface IStockGrain : Orleans.IGrainWithStringKey
{
    Task<string> GetPrice();
}
```

Note, we've opted for an string-based key for our grain, which is useful since the ticker symbol makes a natural key.
The `IGrainWithStringKey` interface is new in the September refresh.
Now add a grain implementation project, and add a reference to the interface project.
Add a reference to `System.Net.Http`.

We'll implement the grain so it retrieves the price of the stock when it is activated:

``` csharp
public class StockGrain : Orleans.Grain, IStockGrain
{
    string price;

    public override async Task OnActivateAsync()
    {
        string stock;
        this.GetPrimaryKey(out stock);
        await UpdatePrice(stock);
        await base.OnActivateAsync();
    }

    async Task UpdatePrice(string stock)
    {
        price = await GetPriceFromYahoo(stock);
    }

    async Task<string> GetPriceFromYahoo(string stock)
    {
        var uri = "http://download.finance.yahoo.com/d/quotes.csv?f=snl1c1p2&e=.csv&s=" + stock;
        using (var http = new HttpClient())
        using (var resp = await http.GetAsync(uri))
        {
            return await resp.Content.ReadAsStringAsync();
        }
    }

    public Task<string> GetPrice()
    {
        return Task.FromResult(price);
    }
}
```


Next create some client code to connect to the Orleans Silo, and retrieve the grain state:

``` csharp
Console.WriteLine("Waiting for Orleans Silo to start. Press Enter to proceed...");
Console.ReadLine();

var config = Orleans.Runtime.Configuration.ClientConfiguration.LocalhostSilo(30000);
GrainClient.Initialize(config);

// retrieve the MSFT stock
var grain = GrainClient.GrainFactory.GetGrain<IStockGrain>("MSFT");
var price = grain.GetPrice().Result;
Console.WriteLine(price);

Console.ReadLine();
```

When we start the local silo, and run the application, we should see the stock value written out

     "MSFT","Microsoft Corpora",37.70,-0.19,"-0.50%"

Note that the extra text in the stock price is just the formatting that Yahoo! returned.

## Refreshing the value with a timer

The problem with the grain as it stands is that the value of the stock will change, but the grain will maintain the same value for it's lifetime (an indefinite period of time).

One way to fix this is to periodically refresh the price.

A traditional .NET timer is not suitable for running in a grain.
Instead, Orleans provides it's own timer.

Let's re-factor the `OnActivateAsync()` method to introduce a timer which will call the `UpdatePrice` method in 1 minute, and then repeatedly every minute from then on, until the grain is deactivated:

``` csharp
public override async Task OnActivateAsync()
{
    string stock;
    this.GetPrimaryKey(out stock);
    await UpdatePrice(stock);

    var timer = RegisterTimer(
        UpdatePrice,
        stock,
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(1));

    await base.OnActivateAsync();
}
```

We'll also have to slightly adjust the `UpdatePrice` method, as the stock argument must be an object rather than a string.
We'll also add some logging so we can see what's happening:

``` csharp
async Task UpdatePrice(object stock)
{
    price = await GetPriceFromYahoo(stock as string);
    Console.WriteLine(price);
}
```

The `RegisterTimer` method takes four arguments:

* `callback` - A function to call.
* `state` - An object to pass as the first argument of the callback function (this can be null).
* `dueTime` - The period to wait before starting the first call to `callback`.
* `period` - The period between subsequent calls to `callback`.


Note: In our sample we're passing the stock name as the  state argument when we register the timer.
This means the stock name is presented to the `UpdatePrice` method as the argument.
Alternative we could set state to be `null`, and read the stock name from inside `UpdatePrice` using `GetPrimaryKey`.
The method returns an `IOrleansTimer` which is disposable and can be used to stop the timer.
It's a good idea to hold on to a reference to this in case you need to stop the timer.

Now when we run the sample, the grain is activated, the timer gets registered and every minute the price is updated for us:

    "MSFT","Microsoft Corpora",37.70,-0.19,"-0.50%"

    "MSFT","Microsoft Corpora",37.70,-0.19,"-0.50%"

    "MSFT","Microsoft Corpora",37.70,-0.19,"-0.50%"

    "MSFT","Microsoft Corpora",37.70,-0.19,"-0.50%"



Orleans is acting as an automatically refreshing cache.
Whenever a stock grain is queried Orleans will provide the latest price it has, without having to make a call to the stock web service.

## Parallelization

Running code in a single threaded execution model, does not prohibit you from awaiting several tasks at once (or in parallel).

Let's add a new function to retrieve the graph data for a stock:

``` csharp
async Task<string> GetYahooGraphData(string stock)
{
    // retrieve the graph data from Yahoo finance
    var uri = string.Format(
        "http://chartapi.finance.yahoo.com/instrument/1.0/{0}/chartdata;type=quote;range=1d/csv/",stock);
    using (var http = new HttpClient())
    using (var resp = await http.GetAsync(uri))
    {
        return await resp.Content.ReadAsStringAsync();
    }
}
```

We'll also add a new field to the grain to store this information:

``` csharp
string graphData;
```

Now we can retrieve the graph data and current price like this:


``` csharp
async Task UpdatePrice(object stock)
{
    price = await GetPriceFromYahoo(stock as string);
    graphData = await GetYahooGraphData(stock as string);
    Console.WriteLine(price);
}
```

However, by doing this we're waiting for the price from Yahoo, and after that's complete we request the graph data.
This is inefficient, as we could be doing these at the same time.
Fortunately, `Task` has a convenient `WhenAll` method which allows us to await multiple tasks at once, allowing these tasks to complete in parallel.


``` csharp
async Task UpdatePrice(object stock)
{
    // collect the task variables without awaiting
    var priceTask = GetPriceFromYahoo(stock as string);
    var graphDataTask = GetYahooGraphData(stock as string);

    // await both tasks
    await Task.WhenAll(priceTask, graphDataTask);

    // read the results
    price = priceTask.Result;
    graphData = graphDataTask.Result;
    Console.WriteLine(price);
}
```

Note: The `Result` of a `Task` will block execution if the task hasn't completed.
This should be avoided in Orleans, tasks should always be awaited before `Result` is read.

Note: When a large number of asynchronous actions need to happen simultaneously you can collect the tasks in a `List<Task<T>>` and present this to `Task.WhenAll`.

## External Tasks

It's tempting to use the [Task Parallel Library](https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/task-parallel-library-tpl) _"TPL"_ for executing parallel tasks in Orleans, but TPL uses the .NET thread pool to dispatch tasks. This is prohibited within grain code.

Orleans has its own task scheduler which provides the single threaded execution model used within grains. 
It's important that when running tasks the Orleans scheduler is used, and not the .NET thread pool.

Should your grain code require a sub-task to be created, you should use `Task.Factory.StartNew`:

``` csharp
await Task.Factory.StartNew(() =>{ /* logic */ });
```


This technique will use the current task scheduler, which will be the Orleans scheduler.

You should avoid using `Task.Run`, which always uses the .NET thread pool, and therefore will not run in the single-threaded execution model.

## Next

Let's look at how Orleans can persist grain state for us:

[Declarative Persistence](Declarative-Persistence.md)
