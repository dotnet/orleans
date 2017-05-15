# Compatible grains

In this document will be described the notion of "backward compatible" and "fully compatible".

## Backward compatible

A grain interface version Vn is said to be backward compatible with Vm if:

  - The name of the interface didn't change (or the overridden typecode)
  - All public methods present in the Vm version are in the Vn version. __It is important that
    the signatures of the methods inherited from Vm are not modified__.

Since Vn can have added methods compared to Vm, Vm is not compatible with Vn.

## Fully compatible

A grain interface version Vn is said to be full compatible with Vm if:

  - Vn is backward compatible with Vm
  - No public methods where added in the Vn version

If Vn is fully compatible with Vm then Vm is also fully compatible with Vn.
