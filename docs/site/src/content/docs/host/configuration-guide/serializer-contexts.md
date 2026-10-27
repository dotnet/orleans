---
title: Compile-time serializer contexts
description: Register a closed graph of Orleans serialization types for NativeAOT.
ms.topic: how-to
---

# Compile-time serializer contexts

A <xref:Orleans.Serialization.SerializerContext> registers a closed graph of types with statically constructed Orleans codecs and deep copiers. Use a context when publishing a serialization component with NativeAOT on .NET 10 and later, so the native compiler can compile each required generic implementation.

## Declare the type graph

Apply <xref:Orleans.GenerateSerializerContextAttribute`1> to a top-level, non-generic partial class derived from `SerializerContext`. The attribute's type argument declares one closed root type, using C# 11 or later. The compiler enforces closed attribute type arguments. The generator follows serialized model members and collection elements, registering every supported dependency. A root such as `List<Dictionary<string, int>>` includes the list, dictionary, string, and integer implementations.

:::code language="csharp" source="snippets/serializer-contexts/SerializerContextExample.cs" id="serializer_context_declaration":::

Models use the existing <xref:Orleans.GenerateSerializerAttribute> and stable <xref:Orleans.IdAttribute> member identifiers. Referenced assemblies provide generated codecs and copiers whose actual constructor and service-member metadata determine the closed dependency graph. NativeAOT implementations use direct member access or static accessors for each serialized member; JIT applications also support generated dynamic accessors. For traversal of referenced models, provide complete implementation metadata to the compiler, for example by setting `ProduceReferenceAssembly=false` on the model project. This preserves the member information consumed by the existing Orleans model generator.

Referenced dependency discovery follows the producer's emitted codec and copier service contracts, including private implementation fields and generated service properties. The producer's selected members and field identifiers therefore determine serialization and copying even when the consumer uses different field-ID settings. Provide complete implementation metadata for models using assembly-level implicit member selection.

Metadata discovery also follows generic arguments, array elements, implemented interfaces, and declaring types. It preserves accessible alias metadata independently of codec registration. This traversal has separate limits of 1,024 closed metadata types and 128 nested dependencies; expanding generic-interface shapes produce `ORLEANS0115` with guidance to declare finite type metadata. Already visited types terminate finite metadata cycles.

The supported graph includes primitive leaf codecs with parameterless construction, generated enums, `List<T>`, `Dictionary<TKey, TValue>`, nullable value types, and single-dimensional zero-based arrays. Generated models use default construction and an `object` base for classes. NativeAOT uses the existing model generator's direct-access and static-accessor support. Model hooks, custom activation, and additional collection families require their corresponding implementation support before being included in a context. The generator reports `ORLEANS0115` for an unsupported dependency or a graph exceeding 1,024 closed types. Native publication diagnoses referenced implementations which require unavailable runtime code generation.

## Register and use the context

<xref:Orleans.Serialization.ServiceCollectionExtensions.AddSerializerContext*> registers the normal <xref:Orleans.Serialization.Serializer> and <xref:Orleans.Serialization.DeepCopier> services. Resolution uses closed factories first, then registered implementation metadata and dependency injection. Repeated calls combine contexts. Duplicate closed registrations use the first registered implementation.

:::code language="csharp" source="snippets/serializer-contexts/SerializerContextExample.cs" id="serializer_context_usage":::

The context's closed serializer registrations seed the type names used during deserialization in the common type resolver's cache. <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AddAllowedType*> authorizes formatted names, while additional names resolve through reflection when the application preserves their metadata. Type-name filters and component validation apply to both forms of resolution. Codecs and copiers preserve Orleans field identifiers, reference tracking, and deep-copy isolation. Object cycles and shared references retain their identity within the restored or copied graph.

Generated struct value serializers and field codecs share one canonical codec instance. Supported non-sealed models register their generated base-codec and base-copier contracts as aliases to the same concrete instances used for field serialization and deep copying. Closed generic models include the concrete array services requested by their generated implementations, while direct byte-array serialization and copying retain the optimized byte-array implementations.

## Dependency construction and concurrency

Generated factories construct known closed concrete codecs and copiers. Generated model code retains its concrete dependency fields. Recursive dependencies reuse partially constructed instances through the provider-specific, caller-aware service-resolution stack and statically closed holders. Dependency holders identify their provider before unwrapping, so dependency-injection-created callers retain the same ownership during recursive resolution. Constructors retain those references. Serialization, copying, activation, and optional shallow-copy queries run after construction completes. Optional copiers cache their completed shallow-copy result when first queried, and subsequent queries and copies reuse that per-instance result.

Each service provider caches one completed instance per implementation type. A transient construction scope on the caller-aware resolution stack owns pending services and typed cache indexes. It acquires the provider's cache lock when a closed factory joins and publishes the complete dependency graph after its outermost constructor succeeds. If construction fails, the scope discards the pending graph and releases its state; the next resolution constructs fresh dependencies, preserving previously completed services. A nested failure caught by a constructor is rethrown by the scope as the original error before publication. A factory cycle encountered before an instance is available faults the graph with guidance to use caller-aware generated-code helpers or statically closed holders for recursive constructor dependencies. Serializer sessions and copy contexts track each operation's reference identities independently.

Closed factories and metadata-based activation compose cyclic dependency graphs in the same resolution pipeline. Automatic services and cache entries created during a factory transaction participate in its publication and rollback. When a metadata constructor enters a closed factory, publication follows completion of that outer metadata constructor. Ordinary metadata activation resolves its dependency-injection services through the application's built provider before a closed factory joins the graph.

During construction, dependencies resolve through provider-owned serialization contracts, metadata-registered implementations, and explicit closed factories registered with <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AddSerializerService*>. Generated model constructors can compose registered collection codecs and copiers with closed element factories in the same graph. Register a shared instance using a factory which returns the captured instance, or provide a closed constructor factory which supplies its external dependencies explicitly. This keeps each pending graph's dependencies stable even when the application builds multiple service providers from a mutable service collection. External dependency-injection lookups during construction produce this registration guidance and fault the pending graph. Outside construction, the service facade delegates ordinary and keyed lookups to the application's built service provider, preserving its registration snapshot, selection rules, and lifetimes.

Reflection-based activation uses constructors preserved by generated manifests, annotated manual registrations, or typed generated dependencies. NativeAOT can materialize a generic implementation when its closed native code is rooted. Context factories make those instantiations visible to the compiler. Forward value-type holders required before ordinary metadata constructors allocate their callers use statically closed constructor or dependency-injection factories. A runtime request for an unavailable native instantiation produces registration guidance; add its closed graph to a context or supply closed factories.

## Diagnose unsupported input

### Serializer initialization and interface collections

For strict NativeAOT publication, initialize the serializer using `AddSerializerContext` with the supported closed type graph described above. <xref:Orleans.Serialization.ServiceCollectionExtensions.AddSerializer*> enables automatic assembly discovery and generalized codecs; native publication analyzes those additional paths, including their runtime activation and metadata requirements. Calling both registration methods combines those paths with the context's closed factories.

The interface collection resolver maps a recognized codec type to the closed collection interface already present in its base-type metadata. NativeAOT can resolve this metadata for rooted codec types while preserving the existing collection aliases and wire representation. A context root or model member declared as an interface collection produces `ORLEANS0115`, identifying that dependency and listing the supported concrete collection shapes. Declare supported concrete collections in the context graph and register every concrete type used at runtime.

`ORLEANS0114` identifies an invalid context declaration. `ORLEANS0115` identifies the unsupported closed type and the required change. Declare every concrete type which can occur in the supported runtime graph, including types introduced through additional roots.

<xref:Orleans.Serialization.CodecNotFoundException> identifies an unregistered runtime type or serialization service. Register its closed graph before requesting serialization or copying.

The generated dictionary registration serializes dictionaries using `EqualityComparer<TKey>.Default`. A custom comparer in a value or serialized payload produces `NotSupportedException`. Applications with custom comparer requirements can register a comparer-aware closed codec through <xref:Orleans.Serialization.Configuration.TypeManifestOptions.AddSerializer*>.

Register that codec and copier before the context so they become the selected implementations. Collection factories bind their element dependencies to the selected per-type registrations, preserving the same comparer behavior for a root dictionary and for dictionaries inside a list. Context-first registration retains the context's default-comparer implementation for both paths.

For a NativeAOT executable, enable native publication:

:::code language="xml" source="snippets/serializer-contexts/NativeContextPublish.props" id="serializer_context_native_publish":::

Import these properties into the executable project and register a context using `AddSerializerContext`. JIT and NativeAOT applications use the same generated-first resolution pipeline. Register the complete graph used by the application so each required codec, copier, and native generic implementation is available.

Keep trim and AOT warnings as errors. The repository's centralized native smoke matrix exercises the strict `Contexts`, `Factories`, and `InterfaceCollections` scenarios on .NET 10, including closed metadata activation, rooted generic materialization, interface collection codec metadata, and reflection type lookup alongside generated registrations.
