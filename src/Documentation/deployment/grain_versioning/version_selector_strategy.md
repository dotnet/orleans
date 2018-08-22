# Version selector strategy

When several versions of the same grain interface exist in the cluster, and a new
activation has to be created, a [compatible version](compatible_grains.md) will be chosen according to the strategy defined in `GrainVersioningOptions.DefaultVersionSelectorStrategy`.

Orleans out of the box supports the following strategies:

## All compatible versions (default)

Using this strategy, the version of the new activation will be chosen randomly 
across all compatible versions.

For example if we have 2 versions of a given grain interface, V1 and V2:

  - V2 is backward compatible with V1
  - In the cluster there are 2 silos that support V2, 8 support V1
  - The request was made from a V1 client/silo

In this case, there is a 20% chance that the new activation will be a V2 and 80%
chance that it will be a V1.

## Latest version

Using this strategy, the version of the new activation will always be the
latest compatible version.

For example if we have 2 versions of a given grain interface, V1 and V2 
(V2 is backward or fully compatible with V1) then all new activations will be V2.

## Minimum version

Using this strategy, the version of the new activation will always be the requested or the
minimum compatible version.

For example if we have 2 versions of a given grain interface, V2, V3, all fully 
compatibles:

  - If the request was made from a V1 client/silo, the new activation will be a V2
  - If the request was made from a V3 client/silo, the new activation will be a V2 too