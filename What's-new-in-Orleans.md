---
layout: page
title: What's new in Orleans
---
{% include JB/setup %}

# Orleans Open Source v1.0 Update (January 2015)

Since the September 2014 Preview Update we have made a small number of public API changes, mainly related to clean up and more consistent naming. Those changes are summarized below:

## Public Type Names Changes

Old API   | New API
------------- | -------------
OrleansLogger | Logger
OrleansClient | GrainClient 
Grain.ActivateAsync | Grain.OnActivateAsync
Grain.DeactivateAsync | Grain.OnDeactivateAsync
Orleans.Host.OrleansSiloHost | Orleans.Runtime.Host.SiloHost 
Orleans.Host.OrleansAzureSilo | Orleans.Runtime.Host.AzureSilo
Orleans.Host.OrleansAzureClient| Orleans.Runtime.Host.zureClient
Orleans.Providers.IOrleansProvider | Orleans.Providers.IProvider
Orleans.Runtime.ActorRuntimeException | Orleans.Runtime.OrleansException
OrleansConfiguration | ClusterConfiguration
LoadAwarePlacementAttribute | ActivationCountBasedPlacementAttribute

## Other Changes

* All grain placement attribute (including [StatelessWorker]) now need to be defined on grain implementation class, rather than on grain interface.
* LocalPlacementAttribute was removed. There are now only StatelessWorker and PreferLocalPlacement.
* Support for Reactive programming with Async RX. 
* Orleans NuGet packages are now published on NuGet.org. 
  See this wiki page for advice on how to [convert legacy Orleans grain interface / class projects over to using NuGet packages](Convert-Orleans-v0.9-csproj-to-Use-v1.0-NuGet).


# September 2014 Preview Update

September 2014 Preview Update is described [here](https://orleans.codeplex.com/wikipage?title=What%27s%20new%20in%20Orleans%3f&referringTitle=Orleans%20Documentation).

