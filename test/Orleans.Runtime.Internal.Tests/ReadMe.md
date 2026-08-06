# TesterInternal Project

This project is for **white-box** testing of the Orleans runtime, 
including testing of internal Orleans runtime APIs.

This project has 'friend' access to the internal API surface of the Orleans runtime.

The following projects use `[InternalsVisibleTo]` assembly attributes to grant internal access privilege to this test project:

- `Orleans.dll`
- `OrleansRuntime.dll`
- `OrleansAzureUtils.dll`

This project may contains a mixture of unit and system tests, including starting test silos.
