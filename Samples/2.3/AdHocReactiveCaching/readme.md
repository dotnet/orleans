# Ad-hoc Reactive Caching Pattern

Orleans Ad-hoc Reactive Caching sample targeting .NET Core 2.1.

This sample demonstrates how to exploit long polling and reentrancy to perform push-speed propagation without observers or streaming.

It demonstrates both one-to-many cache propagation and many-to-one computation propagation via the same approach.

This sample is based upon the reactive polling algorithm as described on [Reactive Caching for Composed Services](https://www.microsoft.com/en-us/research/publication/reactive-caching-for-composed-services/).

## How It Works

The solution contains these grains:

* `ProducerGrain`: Stores an integer value, which it increments by some quantity at some frequency. The quantity and frequency are configurable.
* `ReactiveGrain`: A base grain class that provides a `RegisterReactivePollAsync()` helper method. The grains below derive from this class.
* `ProducerCacheGrain`: A `[StatelessWorker]` grain that keeps up to date with a `ProducerGrain` of the same key via reactive long polling.
* `AggregatorGrain`: Adds the values of two arbitrary `ProducerGrain` by using long polling to keep up to date. Makes its aggregation value available via reactive long polling.
* `AggregatorCacheGrain`: A `[StatelessWorker]` grain that keeps up to date with an `AggregatorGrain` of the same key via reactive long polling.

The grains are set up in this way:

* `ProducerGrain (A)` increments its value by `1` every `5` seconds.
* `ProducerGrain (B)` increments its value by `10` every `15` seconds.
* `ResponseTimeout` on both client and silo are reduced to `10` seconds for this sample. This helps demonstrate long polling to `ProducerGrain (B)` fail every so often and recover.
* `Aggregator (A|B)` long polls `ProducerGrain (A)` and `ProducerGrain (B)`.

* `ProducerCacheGrain (A)`, `ProducerCacheGrain (B)` and `AggregatorCacheGrain (A|B)` long poll their respective producer grains.

The projects are set up in this way:

* `Silo` holds all activations for the sample.
* `Client.A` checks the current value of `ProducerCacheGrain (A)` every second.
* `Client.B` checks the current value of `ProducerCacheGrain (B)` every second.
* `Client.C` checks the current value of `AggregatorCacheGrain (A|B)` every second.

## Build & Run

On Windows, the easiest way to build and run the sample is to execute the `BuildAndRun.ps1` PowerShell script.

Otherwise, use one of the following alternatives...

#### Bash

On bash-enabled platforms, run the `BuildAndRun.sh` bash script.

#### Visual Studio 2017

On Visual Studio 2017, configure solution startup to start these projects at the same time:

* Silo
* Client.A
* Client.B
* Client.C

#### dotnet

To build and run the sample step-by-step on the command line, use the following commands:

1. `dotnet restore` to restore NuGet packages.
2. `dotnet build --no-restore` to build the solution.
3. `dotnet run --project ./src/Silo --no-build` on a separate window to start the silo host.
4. `dotnet run --project ./src/Client.A --no-build` on a separate window to start the first client.
5. `dotnet run --project ./src/Client.B --no-build` on a separate window to start the second client.
6. `dotnet run --project ./src/Client.C --no-build` on a separate window to start the third client.