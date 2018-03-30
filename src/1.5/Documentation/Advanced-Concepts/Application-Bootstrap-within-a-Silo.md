---
layout: page
title: Application Bootstrapping within a Silo
---

[!include[](../../warning-banner.md)]

# Application Bootstrapping within a Silo

There are several scenarios where application want to run some "auto-exec" functions when a silo comes online.

Some examples include, but are not limited to:
* Starting background timers to perform periodic housekeeping tasks
* Pre-loading some cache grains with data downloaded from external backing storage.

We have now added support for this auto-run functionality through configuring "bootstrap providers" for Orleans silos. For example:

``` xml
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <BootstrapProviders>
      <Provider Type="My.App.BootstrapClass1" Name="bootstrap1" />
      <Provider Type="My.App.BootstrapClass2" Name="bootstrap2" />
    </BootstrapProviders>
  </Globals>
</OrleansConfiguration>
```

It is also possible to register Bootstrap provider programaticaly, via calling one of the:

``` csharp
public void RegisterBootstrapProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)

public void RegisterBootstrapProvider<T>(string providerName, IDictionary<string, string> properties = null) where T : IBootstrapProvider
```
on the [`Orleans.Runtime.Configuration.GlobalConfiguration`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Configuration/GlobalConfiguration.cs) class.

These bootstrap providers are C# classes that implement the `Orleans.Providers.IBootstrapProvider` interface.

When each silo starts up, the Orleans runtime will instantiate each of the listed app bootstrap classes, and then call their Init method in an appropriate runtime execution context that allows those classes to act as a client and send messages to grains. There should be no blocking calls made inside the Init method.

``` csharp
Task Init(
    string name,
    IProviderRuntime providerRuntime,
    IProviderConfiguration config)
```

Any Exceptions that are thrown from an Init method of a bootstrap provider will be reported by the Orleans runtime in the silo log, then the silo startup will be halted.

This fail-fast approach is the standard way that Orleans handles silo start-up issues, and is intended to allow any problems with silo configuration and/or bootstrap logic to be easily detected during testing phases rather than being silently ignored and causing unexpected problems later in the silo lifecycle.
