---
layout: page
title: Heterogeneous silos
---

# Heterogeneous silos

## Overview

On a given cluster, silos can support a different set of grain types:
![](../images/Heterogeneous.png)
 
In this example the cluster supports grains of type `A`, `B`, `C`, `D`, `E`:
* Grain types`A`and`B`can be placed on Silo 1 and 2. 
* Grain type`C`can be placed on Silo 1, 2 or 3. 
* Grain type`D`can be only placed on Silo 3
* Grain Type`E`can be only placed on Silo 4.

All silos should reference interfaces of all grain types of the cluster, but grain classes should only be referenced by the silos that will host them.

The client does not know which silo supports a given Grain Type.

**A given Grain Type implementation must be the same on each silo that supports it. The following scenario is NOT valid:**

On Silo 1 and 2:
``` csharp
public class C: Grain, IMyGrainInterface
{
   public Task SomeMethod() { … }
}
```
On Silo 3
``` csharp
public class C: Grain, IMyGrainInterface, IMyOtherGrainInterface
{
   public Task SomeMethod() { … }
   public Task SomeOtherMethod() { … }
}
```

## Configuration

No configuration is needed, you can deploy different binaries on each silo in your cluster.
However, if necessary, you can change the interval that silos and clients check for changes in types supported with the property `TypeMapRefreshInterval` from `TypeManagementOptions`

For testing purposes, you can use the property `ExcludedGrainTypes` in `GrainClassOptions`, which is a list names of the types you want to exclude on the silos.

## Limitations

* Connected clients will not be notified if the set of supported Grain Types changed. In the previous example:
	* If Silo 4 leaves the cluster, the client will still try to make calls to grain of type `E`. It will fail at runtime with a OrleansException.
	* If the client was connected to the cluster before Silo 4 joined it, the client will not be able to make calls to grain of type `E`. It will fail will a ArgumentException
* Stateless grains are not supported: all silos in the cluster must support the same set of stateless grains.

