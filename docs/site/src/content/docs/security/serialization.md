---
title: Serialization security
description: Limit the Orleans serializer type surface and validate data received from clients and storage.
ms.date: 08/17/2026
ms.topic: concept-article
---

# Serialization security

Orleans serializes grain calls, responses, request context, persisted state, reminders, and stream data. Serialization reconstructs application objects from bytes supplied by another process or provider. TLS and provider authentication establish source identity, application policy authorizes the operation, domain validation enforces invariants, and provider controls protect stored bytes.

Limit who can supply serialized input using [network controls](networking.md), [TLS](../host/transport-layer-security.md), provider authentication, and application authorization. Then keep the set of types and data accepted by each boundary as narrow as possible.

## Keep type-name resolution restricted

Orleans-generated serializers use the application's known type manifest. External serializers can support types which include runtime type names, particularly in polymorphic contracts. Registering an external serializer selects a codec, while the type policy determines which CLR types the codec may resolve.

<xref:Orleans.Serialization.Configuration.TypeManifestOptions.AllowAllTypes?displayProperty=nameWithType> defaults to `false`. Preserve that default when any connected client, stream, queue, or storage system can be influenced by a less-trusted party. Enabling it bypasses Orleans type-name validation and permits any resolvable type.

An open type-resolution surface lets input select code paths from types which weren't designed as message contracts. Depending on the configured serializer and available types, deserialization gadget behavior can result in unintended side effects or code execution. An allow list reduces that attack surface, but each allowed type and serializer must still be safe for the data source.

Use the narrowest applicable mechanism:

1. Prefer generated serializers and explicit grain contracts for types the application owns.
1. Add individual polymorphic types with <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AddAllowedType*>.
1. Allow assemblies whose relevant types belong inside the same trust boundary.
1. Implement <xref:Orleans.Serialization.ITypeNameFilter> or <xref:Orleans.Serialization.ITypeFilter> when trust requires an explicit policy.

A denial from a registered type-name filter takes precedence over assembly trust. Constructed generic arguments and array element types are checked independently, so all components must be trusted. See [configure serialization](../host/configuration-guide/serialization-configuration.md#authorize-type-name-resolution) for the supported APIs and examples.

## Treat serializer extensions as security-sensitive

Custom and generalized codecs execute inside the Orleans process. Review them as application code which processes untrusted data:

- Restrict `IsSupportedType` predicates to the intended contracts, and pair namespace-prefix selection with an explicit type policy.
- Reject malformed, truncated, oversized, or semantically invalid values and preserve the previous state.
- Bound collection sizes, nesting, allocations, and CPU work at the codec or application boundary.
- Avoid type-name handling, reflection, callbacks, or constructors that expand the accepted surface beyond the configured policy.
- Configure the same compatible serializer policy on every silo and client which handles the contract.

The [serialization customization guidance](../host/configuration-guide/serialization-customization.md) describes the extension points. Security properties of a third-party serializer or custom codec remain the responsibility of that component and its configuration.

## Validate application data after deserialization

Successful deserialization confirms that bytes match the configured format and type policy. Validate identifiers, lengths, ranges, state transitions, tenant ownership, and other domain invariants before changing grain state or invoking dependencies.

Use dedicated request data-transfer types with the fields required by each grain operation. Keep provider SDK objects, credentials, service containers, and broad domain graphs inside their owning process, and keep secrets in protected stores that stay outside exception messages, request context, grain keys, telemetry, and dashboard-visible fields.

Persisted state and queued stream data follow the provider's trust boundary. Use least-privileged provider identities, encryption and integrity controls supplied by the platform, and separate storage for environments or tenants that require isolation. A serializer allow list reduces the type-resolution surface, while provider authentication and integrity controls establish the provenance of stored data.

For caller and operation controls around serialized grain calls, see [client and grain-call security](authentication-authorization.md).
