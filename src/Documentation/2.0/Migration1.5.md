---
layout: page
title: Migration from Orleans 1.5 to 2.0
---

# Migration from Orleans 1.5 to 2.0

The bulk of the Orleans APIs stayed unchanged in 2.0 or implementation of those APIs were left in legacy classes for backward compatibility. At the same time, the newly introduced APIs provide some new capabilities or better ways of accomplishing those tasks. There are also more subtle differences when it comes to .NET SDK tooling and Visual Studio support that helps to be aware of. This document provides guidance for migrating application code from to Orleans 2.0.

## Visual Studio and Tooling requirements

## Available options for configuration code

## Hosting

## Logging
Orleans 2.0 uses the same logging abstractions as ASP.NET Core 2.0. You can find replacement for most Orleans logging feature in ASP.NET Core logging. Orleans specific logging feature, such as `ILogConsumer` and message bulking, is still maintained in `Microsoft.Orleans.Logging.Legacy` package, so that you still have the option to use them. But how to configure your logging with Orleans changed in 2.0. Let me walk you through the process of migration.

In 1.5, logging configuration is done through `ClientConfiguration` and `NodeConfiguration`. You can configure `DefaultTraceLevel`, `TraceFileName`, `TraceFilePattern`, `TraceLevelOverrides`, `TraceToConsole`, `BulkMessageLimit`, `LogConsumers`, etc through it. In 2.0, logging configuration is consistent with ASP.NET Core 2.0 logging, which means most of the configuration is done through `Microsoft.Extensions.Logging.ILoggingBuilder`. 

To configure `DefaultTraceLevel` and `TraceLevelOverrides`, you need to apply [log filtering](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/logging) to `ILoggingBuilder`. For example, to set trace level to 'Debug' on orleans runtime, you can use sample below, 
```
siloBuilder.AddLogging(builder=>builder.AddFilter("Orleans", LogLevel.Debug));
```
You can configure log level for you application code in the same way. If you want to set a default minimum trace level to be Debug, use sample below
```
siloBuilder.AddLogging(builder=>builder.SetMinimumLevel(LogLevel.Debug);
```
For more information on log filtering, please see their docs on https://docs.microsoft.com/en-us/aspnet/core/fundamentals/logging;

To configure TraceToConsole to be `true`, you need to reference `Microsoft.Extensions.Logging.Console` package and then use `AddConsole()` extension method on `ILoggingBuilder`. The same with `TraceFileName` and `TraceFilePattern`, if you want to log messages to a file, you need to use `AddFile("file name")` method on `ILoggingBuilder`.

If you still want to use Message Bulking feature, You need to configure it through `ILoggingBuilder` as well. Message bulking feature lives in `Microsoft.Orleans.Logging.Legacy` package. So you need to add dependency on that package first. And then configure it through `ILoggingBuilder`. Below is an example on how to configure it with `ISiloHostBuilder`
```
       siloBuiler.AddLogging(builder => builder.AddMessageBulkingLoggerProvider(new FileLoggerProvider("mylog.log")));
```
This method would apply message bulking feature to the `FileLoggerProvider`, with default bulking config.

Since we are going to eventually deprecate and remove LogConsumer feature support in the future, we highly encourage you to migrate off this feature as soon as possible. There's couple approaches you can take to migrate off. One option is to maintain your own `ILoggerProvider`, which creates `ILogger` who logs to all your existing log consumers. This is very similar to what we are doing in `Microsoft.Orleans.Logging.Legacy` package. You can take a look at `LegacyOrleansLoggerProvider` and borrow logic from it. Another option is replace your `ILogConsumer` with existing implementation 
 of `ILoggerProvider` on nuget which provides identical or similar functionality, or implement your own `ILoggerProvider` which fits your specfic logging requirement. And configure those `ILoggerProvider`s with `ILoggingBuilder`.
 
But if you cannot migrate off log consumer in the short term, you can still use it. The support for `ILogConsumer` lives in `Microsoft.Orleans.Logging.Legacy` package. So you need to add dependency on that package first, and then configure Log consumers through extension method `AddLegacyOrleansLogging` on `ILoggingBuilder`.
There's native `AddLogging` method on `IServiceCollection` provided by ASP.NET for you to configure [`ILoggingBuilder`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.loggingservicecollectionextensions.addlogging?view=aspnetcore-2.0#Microsoft_Extensions_DependencyInjection_LoggingServiceCollectionExtensions_AddLogging_Microsoft_Extensions_DependencyInjection_IServiceCollection_System_Action_Microsoft_Extensions_Logging_ILoggingBuilder). We also wrap that method under extension method on `ISiloHostBuilder` and `IClientBuilder`. So you can call `AddLogging` method on silo builder and client builder as well to configure `ILoggingBuilder`. 
below is an example:
```
            var severityOverrides = new OrleansLoggerSeverityOverrides();
            severityOverrides.LoggerSeverityOverrides.Add(typeof(MyType).FullName, Severity.Warning);
            siloBuilder.AddLogging(builder => builder.AddLegacyOrleansLogging(new List<ILogConsumer>()
            {
                new LegacyFileLogConsumer($"{this.GetType().Name}.log")
            }, severityOverrides));
```
You can use this feature if you invested in custom implementation of `ILogConsumer` and cannot convert them to implementation of `ILoggerProvider` in the short term. 
 
` Logger GetLogger(string loggerName)` method on `Grain` base class and `IProviderRuntime`, and `Logger Log { get; }` method on IStorageProvider are still maintained as a deprecated feature in 2.0. You can still use it in your process of migrating off orleans legacy logging. But we recommend you to migrate off them as soon as possible.
 
