---
title: Grain extensions
description: Add runtime-provided interfaces to Orleans grains.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Grain extensions

Grain extensions attach an additional callable interface to grain activations. Orleans uses extensions internally for features such as cancellation and streaming. Applications can use them for infrastructure behavior that should be available across many grain types.

Extensions are an advanced integration mechanism. Prefer a normal grain interface when the behavior is part of the grain's domain contract.

## Define an extension

The interface derives from <xref:Orleans.Runtime.IGrainExtension> and uses normal grain method return types:

:::code language="csharp" source="../snippets/compiled/Grains/GeneralSnippets.cs" id="diagnostics_extension":::
Orleans generates extension request and reference code at build time.

## Register the extension

Register a default implementation on the silo:

:::code language="csharp" source="../snippets/compiled/Grains/GeneralSnippets.cs" id="register_diagnostics_extension":::
The implementation is created through dependency injection for the target grain context.

## Call the extension

Cast an existing grain reference to the extension interface:

:::code language="csharp" source="../snippets/compiled/Grains/GeneralSnippets.cs" id="use_diagnostics_extension":::
The extension reference keeps the same grain identity. It doesn't address a separate grain.

Incoming [grain call filters](interceptors.md) run for extension calls. Filters should not assume every `ImplementationMethod` belongs to the grain implementation class.

## Per-activation components

Framework components can use <xref:Orleans.Runtime.IGrainContext> component APIs and `GetGrainExtension<T>()` to provide an activation-specific extension. Those APIs are intended for runtime integrations that control grain activation setup. Application code should normally use `AddGrainExtension` and constructor-injected dependencies instead of mutating a grain context during activation.

Avoid exposing mutable grain state through a generic extension. Doing so bypasses the grain's domain invariants and couples infrastructure to implementation details.
