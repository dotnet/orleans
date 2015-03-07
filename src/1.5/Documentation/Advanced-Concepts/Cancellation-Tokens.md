---
layout: page
title: Grain cancellation tokens
---

[!include[](../../warning-banner.md)]

# Grain cancellation tokens

The Orleans runtime provides mechanism called grain cancellation token, that enables the developer to cancel an executing grain operation. 


## Description
**GrainCancellationToken** is a wrapper around standard **.NET System.Threading.CancellationToken**, which enables cooperative cancellation between threads, thread pool work items or Task objects, and can be passed as grain method argument. 

A **GrainCancellationTokenSource** is a object that provides a cancellation token through its Token property and sends a cancellation message by calling its  ``Cancel`` method.  

## Usage

* Instantiate a CancellationTokenSource object, which manages and sends cancellation notification to the individual cancellation tokens.

``` csharp
        var tcs = new GrainCancellationTokenSource();
```
* Pass the token returned by the GrainCancellationTokenSource.Token property to each grain method that listens for cancellation.

``` csharp
        var waitTask = grain.LongIoWork(tcs.Token, TimeSpan.FromSeconds(10));
```
* A cancellable grain operation needs to handle underlying **CancellationToken** property of **GrainCancellationToken** just like it would do in any other .NET code.

``` csharp
        public async Task LongIoWork(GrainCancellationToken tc, TimeSpan delay)
        {
            while(!tc.CancellationToken.IsCancellationRequested)
            {
                 await IoOperation(tc.CancellationToken);
            }
        }
```
* Call the ``GrainCancellationTokenSource.Cancel`` method to initiate cancellation. 

``` csharp
        await tcs.Cancel();
```
* Call the ``Dispose`` method when you are finished with the **GrainCancellationTokenSource** object.

``` csharp
        tcs.Dispose();
```


 #### Important Considerations:

* The ``GrainCancellationTokenSource.Cancel`` method returns **Task**, and in order to ensure cancellation the cancel call must be retried in case of transient communication failure.
* Callbacks registered in underlying **System.Threading.CancellationToken** are subjects to single threaded execution guarantees within the grain activation on which they were registered.
* Each **GrainCancellationToken** can be passed through multiple methods invocations. 

