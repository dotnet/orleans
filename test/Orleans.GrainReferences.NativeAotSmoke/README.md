# Production grain-reference NativeAOT smoke

This executable creates source-generated, non-generic grain-reference proxies through
the production `RpcProvider`, `GrainReferenceActivatorProvider`, and cached
`GrainReferenceActivator`. It checks grain and interface identity, inherited
interfaces, version, runtime dependency identity, per-reference keys, shared state,
invocation options, preserved non-public constructors, and direct constructor
exception propagation.

The fixture consumes generated proxy registrations through `AddSerializer`.
It supplies real `TypeConverter` and `CodecProvider` instances with empty payload
manifests to isolate reference construction. Full serializer manifest startup is
tracked separately in [#11368](https://github.com/dotnet/orleans/issues/11368).
Generic proxy construction, payload serialization, and client/silo startup have
their own NativeAOT requirements.

`PublishAot` is set in this project so referenced code-generation projects retain
their normal target frameworks. On a machine with the NativeAOT prerequisites:

```powershell
dotnet publish test\Orleans.GrainReferences.NativeAotSmoke\Orleans.GrainReferences.NativeAotSmoke.csproj --configuration Release --runtime win-x64 --output Artifacts\GrainReferencesNativeAot
.\Artifacts\GrainReferencesNativeAot\Orleans.GrainReferences.NativeAotSmoke.exe
```

The `grain-references-nativeaot-smoke` job in the existing `analyzer-audit.yml`
workflow publishes and executes the smoke for `win-x64` and `linux-x64`. Library trimming and AOT warnings
remain visible in the publish output, and constructor-path diagnostics fail the
workflow. The executable requires dynamic code to be
disabled, so a successful run verifies the native construction branch.
