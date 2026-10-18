---
title: Orleans analyzer diagnostics
description: Reference for Orleans compiler analyzer diagnostic IDs, impacts, fixes, code fixes, and suppression guidance.
ms.date: 10/15/2026
ms.topic: reference
---

# Orleans analyzer diagnostics

Orleans analyzers detect unsupported RPC contracts, serialization schema problems, unsafe grain execution patterns, replay hazards in durable tasks, and compatibility changes. Select a diagnostic ID for its cause, impact, remediation, code-fix behavior, and suppression guidance.

Analyzer help links use `https://aka.ms/orleans/diagnostics` with the diagnostic ID as a URL fragment. The single redirect can therefore remain stable while this index and the detailed pages evolve.

## ORLEANS0001

[Place AlwaysInterleave on the grain interface](orleans0001.md) — Error. `[AlwaysInterleave]` is applied to an implementation method.

## ORLEANS0002

[Reference parameter modifiers are not allowed](orleans0002.md) — Error. A remote interface method uses `ref`, `out`, or `in`.

## ORLEANS0003

[Inherit from Grain](orleans0003.md) — Removed. Current Orleans versions support POCO grain classes.

## ORLEANS0004

[Add missing serialization attributes](orleans0004.md) — Error. A generated-serializer type has an unannotated member.

## ORLEANS0005

[Add GenerateSerializer](orleans0005.md) — Info. A `[Serializable]` type does not opt into Orleans source-generated serialization.

## ORLEANS0006

[Static or abstract members cannot be serialized](orleans0006.md) — Error. An invalid member is assigned an Orleans serialization ID.

## ORLEANS0007

[Use one Orleans activation constructor](orleans0007.md) — Error. Constructor-selection metadata is ambiguous or invalid.

## ORLEANS0008

[Grain interfaces cannot contain properties](orleans0008.md) — Error. An Orleans remote interface declares an instance property.

## ORLEANS0009

[Use a registered grain-call return type](orleans0009.md) — Error. Orleans cannot map a remote method return type to an invokable request.

## ORLEANS0010

[Add a stable Alias](orleans0010.md) — Info. A type or RPC method uses a name-derived identity.

## ORLEANS0011

[Rename a duplicated Alias](orleans0011.md) — Error. Type or method aliases collide.

## ORLEANS0012

[Change a duplicated serialization Id](orleans0012.md) — Error. Members of one serialized type reuse a field ID.

## ORLEANS0013

[Remove serialization identity attributes from a grain class](orleans0013.md) — Error. A grain implementation is marked for data serialization.

## ORLEANS0014

[Preserve the grain execution context](orleans0014.md) — Warning. `ConfigureAwait` can move a continuation outside the grain scheduler.

## ORLEANS0016

[Grain interface is not active in OrleansContracts.txt](orleans0016.md) — Warning. A grain interface is missing or retired in the manifest.

## ORLEANS0017

[Grain interface version mismatch](orleans0017.md) — Warning. Source and manifest versions differ.

## ORLEANS0018

[Grain interface member not declared](orleans0018.md) — Warning. An RPC signature is absent from the manifest.

## ORLEANS0019

[Removed grain interface is not retired](orleans0019.md) — Warning. An active manifest interface no longer exists in source.

## ORLEANS0020

[OrleansContracts.txt is missing](orleans0020.md) — Info. Contract analysis is enabled without a manifest.

## ORLEANS0021

[Duplicate grain interface declaration](orleans0021.md) — Warning. The manifest repeats an interface name or identity.

## ORLEANS0022

[Grain class is not active in OrleansContracts.txt](orleans0022.md) — Warning. A grain class is missing or retired in the manifest.

## ORLEANS0023

[Grain class identity mismatch](orleans0023.md) — Warning. Source and manifest grain types differ.

## ORLEANS0024

[Removed grain class is not retired](orleans0024.md) — Warning. An active manifest grain class no longer exists in source.

## ORLEANS0025

[Duplicate grain class declaration](orleans0025.md) — Warning. The manifest repeats a grain class name or identity.

## ORLEANS0026

[Invalid invokable base type mapping](orleans0026.md) — Error. A custom grain-call return type mapping cannot generate a valid invokable request.

## ORLEANS0027

[Grain interface member removed from source](orleans0027.md) — Warning. The manifest retains an RPC signature which is absent from source.

## ORLEANS0028

[Suppress CS1998 for async durable task methods](orleans0028.md) — Suppressor. An `async DurableTask` method which completes without awaiting is a normal durable step.

## ORLEANS0100

[Access grain state in recorded effects or after the last durable operation](orleans0100.md) — Warning. Durable task code reads or changes grain state where a replay runs it again.

## ORLEANS0101

[Produce non-deterministic values in recorded effects](orleans0101.md) — Warning. Durable task code produces a clock, random, or environment value where a replay runs it again.

## ORLEANS0102

[Await external operations in steps or after the last durable operation](orleans0102.md) — Warning. Durable task code awaits a service or other external work where a replay runs it again.

## ORLEANS0103

[Change grain state after the last external await](orleans0103.md) — Warning. A step or a body's tail changes grain state before an external await, so the change can commit without the result.

## ORLEANS0104

[Return the outputs of a DurableTask.RunInline effect](orleans0104.md) — Warning. A `DurableTask.RunInline` effect assigns a variable of the body, which a replay doesn't assign.

## ORLEANS0105

[Await, schedule, or post durable task definitions](orleans0105.md) — Warning. Durable task code creates a durable task definition and never runs it.

## ORLEANS0106

[Run each durable task definition once](orleans0106.md) — Warning. Durable task code runs a single-use durable task definition more than once.

## ORLEANS0107

[Use durable operation names without '$'](orleans0107.md) — Error. A `WithId` or `DurableTask.RunInline` name contains `$`, which the runtime rejects.

## ORLEANS0108

[Give each durable operation call site its own name](orleans0108.md) — Warning. Two call sites in one body name their durable operations alike, so their identifiers depend on order.

## ORLEANS0109

[Keep durable task bodies on their scheduler](orleans0109.md) — Warning. Durable task code starts threads, timers, or thread-pool work off the activation's scheduler.

## ORLEANS0110

[Run concurrent durable work through durable combinators](orleans0110.md) — Warning. Durable task code combines durable work with a `Task` combinator instead of a durable combinator.

## ORLEANS0111

[Wait with durable delays](orleans0111.md) — Warning. Durable task code waits with `Task.Delay` or `Thread.Sleep`, which a replay restarts.

## ORLEANS0112

[Filter cancellation handlers by IsTaskCancellationRequested](orleans0112.md) — Info. Durable task code catches cancellation without telling task cancellation from attempt cancellation.

## ORLEANS0113

[Return a task from asynchronous code in durable task bodies](orleans0113.md) — Warning. A durable task body contains `async void` code, which the body can't observe.

## ORLEANS0114

[Derive a signal name for each repeated wait](orleans0114.md) — Info. Durable task code waits for one signal name in a loop, so every wait returns the first value.

## ORLEANS0115

[Log through a replay-safe logger](orleans0115.md) — Info. Durable task code logs through a logger which logs again on replay.

## ORLEANS0116

[Post durable tasks from grains or durable task bodies](orleans0116.md) — Warning. Code outside grains and durable task bodies posts a durable task, which throws.
