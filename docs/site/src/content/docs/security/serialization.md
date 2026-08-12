---
title: Serialization security
description: Limit the Orleans serializer type surface and validate data received from clients and storage.
ms.date: 08/12/2026
ms.topic: concept-article
---

# Serialization security

Orleans serializes grain calls, responses, request context, persisted state, reminders, and stream data. Serialization reconstructs application objects from bytes supplied by another process or provider. It doesn't authenticate the sender, authorize the requested operation, validate application invariants, encrypt data, or prove that stored data hasn't been modified.

Limit who can supply serialized input using [network controls](networking.md), [TLS](../host/transport-layer-security.md), provider authentication, and application authorization. Then keep the set of types and data accepted by each boundary as narrow as possible.

## Keep type-name resolution restricted

Orleans-generated serializers use the application's known type manifest. External serializers can support types which include runtime type names, particularly in polymorphic contracts. Registering an external serializer selects a codec; it doesn't authorize every CLR type that codec could resolve.

<xref:Orleans.Serialization.Configuration.TypeManifestOptions.AllowAllTypes?displayProperty=nameWithType> defaults to `false`. Preserve that default when any connected client, stream, queue, or storage system can be influenced by a less-trusted party. Enabling it bypasses Orleans type-name validation and permits any resolvable type.

An open type-resolution surface lets input select code paths from types which weren't designed as message contracts. Depending on the configured serializer and available types, deserialization gadget behavior can result in unintended side effects or code execution. An allow list reduces that attack surface, but each allowed type and serializer must still be safe for the data source.

Use the narrowest applicable mechanism:

1. Prefer generated serializers and explicit grain contracts for types the application owns.
1. Add individual polymorphic types with <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AddAllowedType*>.
1. Allow an assembly only when every relevant type in that assembly belongs inside the same trust boundary.
1. Implement <xref:Orleans.Serialization.ITypeNameFilter> or <xref:Orleans.Serialization.ITypeFilter> when trust requires an explicit policy.

A denial from a registered type-name filter takes precedence over assembly trust. Constructed generic arguments and array element types are checked independently, so all components must be trusted. See [configure serialization](../host/configuration-guide/serialization-configuration.md#authorize-type-name-resolution) for the supported APIs and examples.

## Treat serializer extensions as security-sensitive

Custom and generalized codecs execute inside the Orleans process. Review them as application code which processes untrusted data:

- Restrict `IsSupportedType` predicates to the intended contracts. A namespace-prefix predicate is selection logic, not authorization.
- Reject malformed, truncated, oversized, or semantically invalid values without partially applying state changes.
- Bound collection sizes, nesting, allocations, and CPU work for formats which don't already enforce suitable limits.
- Avoid type-name handling, reflection, callbacks, or constructors that expand the accepted surface beyond the configured policy.
- Configure the same compatible serializer policy on every silo and client which handles the contract.

The [serialization customization guidance](../host/configuration-guide/serialization-customization.md) describes the extension points. Security properties of a third-party serializer or custom codec remain the responsibility of that component and its configuration.

## Validate application data after deserialization

Successfully deserialized data isn't necessarily valid or authorized. Validate identifiers, lengths, ranges, state transitions, tenant ownership, and other domain invariants before changing grain state or invoking dependencies.

Use dedicated request data-transfer types instead of passing provider SDK objects, credentials, service containers, or broad domain graphs across grain interfaces. Don't include secrets in exception messages, request context, grain keys, or fields which telemetry and dashboard tooling can display.

Persisted state and queued stream data should be treated according to the provider's trust boundary. Use least-privileged provider identities, encryption and integrity controls supplied by the platform, and separate storage for environments or tenants that require isolation. A serializer allow list reduces the type-resolution surface; it doesn't make a compromised provider trustworthy.

For caller and operation controls around serialized grain calls, see [client and grain-call security](authentication-authorization.md).
