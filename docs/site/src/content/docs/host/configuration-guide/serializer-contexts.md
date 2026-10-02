---
title: Compile-time serializer contexts
description: Register a closed graph of Orleans serialization types for NativeAOT.
ms.topic: how-to
---

# Compile-time serializer contexts

A <xref:Orleans.Serialization.SerializerContext> registers a closed graph of types with statically constructed Orleans codecs and deep copiers. Use a context when publishing a serialization component with NativeAOT, so the native compiler can compile each required generic implementation.

## Declare the type graph

Apply <xref:Orleans.GenerateSerializerContextAttribute> to a top-level, non-generic partial class derived from `SerializerContext`. Each attribute declares one closed root type. The generator follows serialized model members and collection elements, registering every supported dependency. A root such as `List<Dictionary<string, int>>` includes the list, dictionary, string, and integer implementations.

:::code language="csharp" source="snippets/serializer-contexts/SerializerContextExample.cs" id="serializer_context_declaration":::

Models use the existing <xref:Orleans.GenerateSerializerAttribute> and stable <xref:Orleans.IdAttribute> member identifiers. Referenced assemblies can provide generated model codecs and copiers; the context constructs their closed implementations and validates the producer's generated accessor contract. Compile those implementations with direct member access or static accessors for each serialized member. For traversal of referenced models, provide the implementation assembly to the compiler, for example by setting `ProduceReferenceAssembly=false` on the model project. This preserves the member information consumed by the existing Orleans model generator.

Referenced dependency discovery follows the producer's emitted codec and copier service contracts, including private implementation fields and generated service properties. The producer's selected members and field identifiers therefore determine serialization and copying even when the consumer uses different field-ID settings. Provide complete implementation metadata for models using assembly-level implicit member selection.

The supported graph includes primitive leaf codecs with parameterless construction, generated enums, `List<T>`, `Dictionary<TKey, TValue>`, nullable value types, and single-dimensional zero-based arrays. Generated models use default construction, an `object` base for classes, and members supported by direct access or statically generated accessors. The context generator uses the existing model generator's accessor guarantees. Model hooks, custom activation, and additional collection families require their corresponding implementation support before being included in a context. The generator reports `ORLEANS0115` for a dependency requiring dynamic field access, another unsupported dependency, or a graph exceeding 1,024 closed types.

## Register and use the context

<xref:Orleans.Serialization.ServiceCollectionExtensions.AddSerializerContext*> registers the normal <xref:Orleans.Serialization.Serializer> and <xref:Orleans.Serialization.DeepCopier> services. Resolution uses closed factories first, then registered implementation metadata and dependency injection. Repeated calls combine contexts. Duplicate closed registrations use the first registered implementation.

:::code language="csharp" source="snippets/serializer-contexts/SerializerContextExample.cs" id="serializer_context_usage":::

The context also registers the closed type names used during deserialization in the common type resolver's cache. Additional names resolve through reflection when the application preserves their metadata. Type-name filters and component validation apply to both forms of resolution. Codecs and copiers preserve Orleans field identifiers, reference tracking, and deep-copy isolation. Object cycles and shared references retain their identity within the restored or copied graph.

Generated struct value serializers and field codecs share one canonical codec instance. Closed generic models include the concrete array services requested by their generated implementations, while direct byte-array serialization and copying retain the optimized byte-array implementations.

## Dependency construction and concurrency

Generated factories construct known closed concrete codecs and copiers. Generated model code retains its concrete dependency fields. Recursive dependency edges use the existing caller-aware service-resolution stack and statically closed holders. Constructors retain dependency references; serialization and copying begin after construction completes.

Each service provider caches one completed instance per implementation type. Construction is serialized per provider. A construction transaction publishes the complete dependency graph after its outermost constructor succeeds. If construction fails, the pending graph is discarded and the next resolution constructs fresh dependencies, preserving previously completed services. Serializer sessions and copy contexts track each operation's reference identities independently.

Closed factories can use acyclic metadata-based dependencies in the same resolution pipeline. Automatic cache entries created during a factory transaction participate in its publication and rollback. A constructor cycle combining automatic activation and closed factories produces a diagnostic requesting closed factories for every service in that cycle. Dependency injection reuses previously published services; a DI singleton requesting an unpublished serialization dependency receives guidance to register that participant through `AddSerializerService`.

Reflection-based activation uses constructors preserved by generated manifests, annotated manual registrations, or typed generated dependencies. NativeAOT can materialize a generic implementation when its closed native code is rooted. Context factories make those instantiations visible to the compiler. A runtime request for an unavailable native instantiation produces registration guidance; add its closed graph to a context or supply closed factories.

## Diagnose unsupported input

`ORLEANS0114` identifies an invalid context declaration. `ORLEANS0115` identifies the unsupported closed type and the required change. Declare every concrete type which can occur in the supported runtime graph, including types introduced through additional roots.

<xref:Orleans.Serialization.CodecNotFoundException> identifies an unregistered runtime type or serialization service. Register its closed graph before requesting serialization or copying.

The generated dictionary registration serializes dictionaries using `EqualityComparer<TKey>.Default`. A custom comparer in a value or serialized payload produces `NotSupportedException`. Applications with custom comparer requirements can register a comparer-aware closed codec through <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AddSerializer*>.

For a NativeAOT executable, enable native publication:

:::code language="xml" source="snippets/serializer-contexts/NativeContextPublish.props" id="serializer_context_native_publish":::

Import these properties into the executable project and register a context using `AddSerializerContext`. JIT and NativeAOT applications use the same generated-first resolution pipeline. Register the complete graph used by the application so each required codec, copier, and native generic implementation is available.

Keep trim and AOT warnings as errors. The repository's centralized native smoke matrix exercises the strict `Contexts` and `Factories` scenarios on .NET 10, including closed metadata activation, rooted generic materialization, and reflection type lookup alongside generated registrations.
