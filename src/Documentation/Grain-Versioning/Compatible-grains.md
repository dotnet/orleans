# Compatible grains

When an existing grain activation is about to process a request, the runtime will check if the version
in the request and the actual version of the grain are compatible.
__Orleans does not infer at runtime which policy to use__,
The default behavior to determine if two versions are compatible is determined by `GrainVersioningOptions.CompatibilityStrategy`

## Backward compatible (default)

### Definition

A grain interface version Vn can be be backward compatible with Vm if:

  - The name of the interface didn't change (or the overridden typecode)
  - All public methods present in the Vm version are in the Vn version. __It is important that
    the signatures of the methods inherited from Vm are not modified__: since Orleans use
    an internal built-in serializer, modifying/renaming a field (even private) can make the
    serialization to break.

Since Vn can have added methods compared to Vm, Vm is not compatible with Vn.

### Example

If in the cluster we have two versions of a given interface, V1 and V2 and that V2 is backward compatible
with V1:

  - If the current activation is a V2 and the requested version is V1, the current activation will
    be able to process the request normally
  - If the current activation is a V1 and the requested version is V2, the current activation will be
    deactivated and a new activation compatible with V2 will be created (see [version selector strategy](Version-selector-strategy.md)).

## Fully compatible

### Definition

A grain interface version Vn can be fully compatible with Vm if:

  - Vn is backward compatible with Vm
  - No public methods where added in the Vn version

If Vn is fully compatible with Vm then Vm is also fully compatible with Vn.

### Example

If in the cluster we have two versions of a given interface, V1 and V2 and that V2 is fully compatible
with V1:

  - If the current activation is a V2 and the requested version is V1, the current activation will
    be able to process the request normally
  - If the current activation is a V2 and the requested version is V1, the current activation will also
    be able to process the request normally