---
title: Customize Orleans serialization code generation
description: Extend Orleans RPC code generation with custom grain-call return types and generated invokable request bases.
ms.date: 08/20/2026
ms.topic: how-to
---

# Customize Orleans serialization code generation

Orleans generates the proxy and request types which implement grain calls. Advanced libraries can extend that generated RPC surface by associating a return type with a custom invokable request base. The return type defines the application-facing calling model, while the request base defines how the generated request enters the Orleans runtime and how the target result becomes a response.

Use this extension point when a library needs a calling abstraction with explicit semantics beyond <xref:System.Threading.Tasks.Task>, <xref:System.Threading.Tasks.ValueTask>, or <xref:System.Collections.Generic.IAsyncEnumerable`1>. The library owns the abstraction's completion, cancellation, failure, lifetime, allocation, and concurrency contract.

For a complete application, see the custom grain-call return type entry in the [maintained samples catalog](https://github.com/dotnet/orleans/blob/main/samples/README.md).

## Define an awaitable return type

The following `GrainCall<T>` is task-backed. Calling a generated proxy starts one Orleans request immediately. The returned value can be awaited multiple times, caches its terminal result, and propagates remote failures through the task.

:::code language="csharp" source="../../snippets/compiled/Serialization/CustomGrainCallReturnType.cs" id="grain_call_return_type":::

<xref:Orleans.InvokableBaseTypeAttribute> registers the open generic family for proxies derived from <xref:Orleans.Runtime.GrainReference>. For every `GrainCall<T>` method, the generator closes `GrainCallRequest<T>` with the same `T`.

## Adapt the generated request

The request base performs two jobs:

1. On the caller, its initializer submits the generated request through <xref:Orleans.Runtime.IGrainReferenceRuntime> and returns the application-facing `GrainCall<T>`.
2. On the target, its <xref:Orleans.Serialization.Invocation.IInvokable.Invoke*> implementation awaits the value returned by the grain implementation and creates a <xref:Orleans.Serialization.Invocation.Response>.

:::code language="csharp" source="../../snippets/compiled/Serialization/CustomGrainCallReturnType.cs" id="grain_call_request":::

<xref:Orleans.Invocation.ReturnValueProxyAttribute> tells the generated proxy to return `request.InitializeRequest(this)`. The initializer is therefore the handoff point which starts the operation or creates the application-facing adapter. Orleans validates that overload resolution selects an accessible, concrete, non-generic instance method with one by-value parameter accepting the generated proxy and a result implicitly convertible to the grain method's declared return type.

<xref:Orleans.GeneratedActivatorConstructorAttribute> selects a dependency-injected constructor for generated request activation. A parameterless constructor also works when the request base needs no services. Mark runtime-only fields with <xref:System.NonSerializedAttribute>; generated argument fields remain the serialized request payload.

## Use the return type in a grain contract

Both the interface and implementation use the custom return type. The generated request overrides `InvokeInner` with that same signature, while the request base determines how its value is completed and transported.

:::code language="csharp" source="../../snippets/compiled/Serialization/CustomGrainCallReturnType.cs" id="grain_call_contract":::

The sample's contract is:

- **Completion:** the proxy initializer submits one request immediately, and the task completes when the Orleans response arrives.
- **Failure:** synchronous grain failures and failures produced while awaiting `GrainCall<T>` become Orleans exception responses and are rethrown to the caller.
- **Cancellation:** a `CancellationToken` grain argument participates in Orleans cooperative call cancellation. The sample wrapper adds no independent cancellation source.
- **Lifetime:** the runtime owns the generated request after submission. The wrapper retains the task representing that invocation.
- **Concurrency:** the task-backed wrapper supports multiple awaiters observing the same terminal result. It represents one invocation and never resubmits it.

Custom adapters can implement other policies, including lazy submission, streaming, or subscriptions. Specify those policies as part of the public return type contract and account for grain activation lifetime, disposal, backpressure, and abandoned consumers.

## Registration locations and precedence

<xref:Orleans.InvokableBaseTypeAttribute> can appear in four places:

| Registration location | Purpose |
| --- | --- |
| An attribute type applied to a grain method | Select behavior for methods carrying that attribute. |
| The return type | Define the normal adapter owned by that return-type library. |
| An assembly | Connect a return type and proxy base owned by independent libraries. |
| A proxy base through <xref:Orleans.DefaultInvokableBaseTypeAttribute> | Define the proxy's built-in return families. |

Resolution runs in two passes. Orleans first considers exact constructed return-type matches, then open-generic matches. Within each pass, precedence is method attribute, return type, assembly registration, and proxy default. Consequently, an exact assembly registration has priority over an open-generic method registration.

Every registration is scoped to the proxy base's original generic definition. A mapping for `GrainReference` does not affect another proxy hierarchy. An assembly registration can add a mapping, including one supplied by a referenced adapter assembly, and cannot replace a proxy's built-in default mapping.

Identical registrations coalesce. Distinct invokable bases at the same winning location produce a deterministic build diagnostic ordered by type and assembly identity. This keeps reference ordering from changing generated behavior.

## Exact and open-generic mappings

An exact mapping associates one constructed return type, such as `GrainCall<int>`, with one request base. It overrides an open mapping for `GrainCall<>`.

An open-generic return mapping requires an open-generic request base with the same arity. Orleans closes the request base with the return type arguments and verifies every generic constraint. A closed request base cannot serve an open return family.

Use an exact mapping for a specialized protocol or optimization. Keep the open mapping as the family-wide contract so new constructed return types receive consistent behavior.

## Validation requirements

The generator validates a selected request base in the consuming compilation:

- It is an accessible, non-static, non-sealed class.
- Its generic arity matches the open return type and the constructed type arguments satisfy its constraints.
- A generated derived request can invoke an accessible parameterless constructor, including an unambiguous optional or `params` constructor, or an accessible constructor marked with <xref:Orleans.GeneratedActivatorConstructorAttribute>.
- A dependency-injected constructor has by-value parameters and binds unambiguously through the generated derived constructor.
- A <xref:Orleans.Invocation.ReturnValueProxyAttribute> initializer binds from the generated proxy type and returns the declared custom return type.

Treat these diagnostics as extension-contract failures. They identify the registration site so the library can correct its published mapping.

## Cross-assembly libraries

An adapter package can register types from independent assemblies:

:::code language="csharp" source="../../snippets/compiled/Serialization/CustomGrainCallReturnType.cs" id="cross_assembly_registration":::

The consuming project must reference the return-type owner, proxy owner, and adapter assembly. The generator reads assembly attributes from source and referenced assemblies, then validates accessibility and binding in the consuming compilation. Public types and members give every consumer the same mapping; `internal` members require an explicit friend-assembly relationship with each generated proxy assembly.

Publish the return type, request base, registration, and any required serializer metadata as one versioned compatibility unit. Validate consumers which generate proxies in a different assembly and deployments where callers and silos run adjacent package versions.

## Identity, serialization, and compatibility

The generated request type is the serialized message body. Orleans gives it a compound identity containing the invocation marker, proxy identity, grain interface type, and method identity. Method identity comes from <xref:Orleans.IdAttribute>, <xref:Orleans.AliasAttribute>, or a deterministic signature hash. Explicit aliases preserve identity when CLR names change.

The custom request base controls invocation behavior, while generated members hold method arguments and target dispatch metadata. Keep serialized member IDs and aliases stable, and keep argument and result types compatible during rolling upgrades. Changing a mapping can change the generated request's base behavior across caller and silo versions even when the grain method signature is unchanged.

The same Orleans.Serialization codecs, copiers, activators, and converters process generated request arguments and response values. Review the [serialization and code-generation internals](../../implementation/serialization.md) before publishing an adapter library.

## Related customization surfaces

- [Customize serialization](serialization-customization.md) with <xref:Orleans.Serialization.Serializers.IGeneralizedCodec>, <xref:Orleans.Serialization.Cloning.IGeneralizedCopier>, and type filters.
- [Configure serialization](serialization-configuration.md) and register codecs, copiers, activators, converters, or external serializers through <xref:Orleans.Serialization.ISerializerBuilder>.
- [Declare immutable types](serialization-immutability.md) with <xref:Orleans.ImmutableAttribute>.
- [Generate serializers and stable identities](../../grains/code-generation.md) with <xref:Orleans.GenerateSerializerAttribute>, <xref:Orleans.IdAttribute>, and <xref:Orleans.AliasAttribute>.
- [Inspect or modify generated request arguments](../../grains/interceptors.md) in grain call filters.
- [Use generated request metadata for scheduling](../../grains/request-scheduling.md).
