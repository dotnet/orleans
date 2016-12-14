---
layout: page
title: Heterogeneous silos
---

# Heterogeneous silos
On a given cluster, silos can support a different set of grain types:
 
In this example the cluster support grain of type A, B, C, D, E:
- Grain types A and B can be placed on Silo 1 and 2. 
- Grain type C can be placed on Silo 1, 2 or 3. 
- Grain type D can be only placed on Silo 3
- Grain Type E can be only placed on Silo 4.

The client does not know which silo support a given Grain Type.

A given Grain Type implementation must be the same on each silo that support it. The following scenario is NOT valid:

On Silo 1 and 2:
```
public class C: IMyGrainInterface
{
   public Task SomeMethod() { … }
}
```
On Silo 3
```
public class C: IMyGrainInterface, IMyOtherGrainInterface
{
   public Task SomeMethod() { … }
   public Task SomeOtherMethod() { … }
}
```
## Limitations:
- Connected clients will not be notified if the set of supported Grain Types changed. In the previous example:
If Silo 4 leave the cluster, the client will still try to make calls to grain of type E. It will fail at runtime with a OrleansException.
If the client was connected to the cluster before Silo 4 joined it, the client will not be able to make calls to grain of type E. It will fail will a ArgumentException
- Stateless grains is not supported: all silos in the cluster must support the same set of stateless grains.

