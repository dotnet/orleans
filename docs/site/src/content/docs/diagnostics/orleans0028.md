---
title: "ORLEANS0028: Grain contract data types require serialization support"
description: Enable strict compile-time checks for missing serialization support in grain request and response graphs.
ms.topic: reference
---

# ORLEANS0028: Grain contract data types require serialization support

| Property | Value |
| --- | --- |
| Category | Usage |
| Severity | Warning, disabled by default |
| Code fix | Not available |

## Enable the diagnostic

Add this setting to the project's `.editorconfig` to require statically declared serialization support for grain contracts:

```ini
[*.cs]
dotnet_diagnostic.ORLEANS0028.severity = warning
```

The rule is opt-in because runtime serializer configuration is outside the compiler's view.

## Cause

A concrete request, response, or nested data type in a grain RPC contract has neither <xref:Orleans.GenerateSerializerAttribute> nor a statically registered Orleans serializer.

The analyzer finds transitive <xref:Orleans.IGrain> and <xref:Orleans.IGrainObserver> interfaces, including inherited ordinary public instance methods and closed generic base interfaces. It also checks contracts from referenced assemblies at their grain implementations. Diagnostics identify the missing type and the contract method, with a source location on the request or response declaration, or on the implementing grain when the contract comes from metadata.

The traversal follows asynchronous response wrappers, array elements, built-in collection data arguments, model base types, and members selected by explicit <xref:Orleans.IdAttribute> values, type-level implicit field IDs, or constructor-parameter serialization. Constructor selection honors records and the `IncludePrimaryConstructorParameters` option on ordinary classes. Generic model arguments are checked through selected members, so an unused type argument does not require a serializer. Non-serialized members and local implementation helpers are excluded.

## Impact

A forgotten serializer annotation can otherwise remain unnoticed until a request or response reaches a missing codec at runtime. Enabling this rule moves statically visible missing support into the build.

## How to fix

Mark owned DTOs with <xref:Orleans.GenerateSerializerAttribute> and give their serialized members stable IDs. Resolve [ORLEANS0004](orleans0004.md) for missing member annotations. For an externally owned or custom-serialized type, provide an appropriate explicitly registered codec or surrogate converter.

Built-in scalar codecs, enums, grain references, and recognized registered serializers are accepted. Built-in generic collection codecs also check their data arguments. For a model without `GenerateSerializer`, a custom codec owns its serialized representation and is treated as opaque. Annotated models are checked through their declared schema even when a codec is registered. A registered converter's surrogate is checked as a separate data type.

Grain implementation classes represent activations and do not need a data serializer. Applying the attribute to a grain class can instead produce [ORLEANS0013](orleans0013.md).

## Static-analysis boundary and suppression

This rule checks declared types, not every concrete value which can occur at runtime. Values behind `object`, interface types, or open type parameters can introduce runtime subtypes which require their own serializers. A successful check does not prove runtime codec registration, code generation in another project, or a serialization round trip.

Finite recursive graphs are checked once per constructed type. The traversal is bounded to 64 levels and 1,024 distinct types per request or response to keep analysis responsive, including expanding generic models and converter chains. When a limit is reached, the warning explicitly reports that the graph could not be fully checked; it does not claim that a serializer is missing. Verify those graphs with serialization round-trip tests before suppressing the warning.

Runtime-registered generalized serializers, arbitrary dependency-injection factories, assembly-level implicit field-ID settings in referenced projects, and custom serializer registrations with arbitrary generic constraints cannot always be established statically. Keep the rule disabled or suppress a specific diagnostic when independently verified runtime configuration supplies that support. Use a serialization round-trip test to verify those paths.

For referenced models using type-level implicit field IDs, read-only auto-properties are conservatively included when private backing fields are hidden from compiler metadata. A field-level exclusion in the producer can therefore require a consumer-side suppression after checking the producer's schema.

Use [ORLEANS0029](orleans0029.md) separately for grain state used by Orleans-serialized storage or live activation migration. JSON storage alone does not remove the serialization requirement for migration.
