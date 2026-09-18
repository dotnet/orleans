---
title: Use durable state
description: Build an experimental Orleans Journaling grain with durable values and collections.
ms.date: 08/21/2026
ms.topic: how-to
---

# Use durable state

Install the pre-release [`Microsoft.Orleans.Journaling`](https://www.nuget.org/packages/Microsoft.Orleans.Journaling) package in the silo project. Install a [journal storage provider](configuration.md#choose-a-storage-provider) and configure it before activating grains which use durable state.

All Journaling APIs are experimental and carry diagnostic `ORLEANSEXP005`.

## Define a durable grain

Inject <xref:Orleans.Journaling.IDurableStateManager> into an ordinary <xref:Orleans.Grain> and declare named state components during construction. The manager creates each component once and Orleans recovers the grain's state before application methods run:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingSnippets.cs" id="composed_shopping_cart":::

The standard manager enrolls itself in the grain lifecycle when constructed with the activation's <xref:Orleans.Runtime.IGrainContext>, before resolution returns. Declare durable state components during construction or synchronous activation setup. Recovery at <xref:Orleans.Runtime.GrainLifecycleStage.SetupState> finishes before <xref:Orleans.Grain.OnActivateAsync*> and request processing. The same composition works with an application-owned grain base class.

The dictionary mutation is immediately visible to the current activation. Awaiting <xref:Orleans.Journaling.IDurableStateManager.WriteStateAsync*> establishes the durability point for pending mutations to the grain's state. Accept a <xref:System.Threading.CancellationToken> on grain operations and flow it through state-manager calls so cancellation follows the caller's operation lifetime.

The composition example is compiled against repository source so it exercises constructor-owned lifecycle enrollment.

<xref:Orleans.Journaling.IDurableStateManager.GetOrAddState*> accepts an application contract, such as `IDurableDictionary<string, int>`. A registered factory supplies the implementation. The <xref:Orleans.Journaling.DurableStateManagerExtensions> helpers provide the same access with discoverable names.

### Use keyed injection and the convenience base class

<xref:Orleans.Journaling.DurableGrain> supplies a <xref:Orleans.Journaling.DurableGrain.StateManager> property typed as `IDurableStateManager` and a protected <xref:Orleans.Journaling.DurableGrain.WriteStateAsync*> forwarding helper. Inject a state component with <xref:Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute> when its name is fixed:

:::code language="csharp" source="./snippets/journaling/JournalingBasics.cs" id="keyed_durable_counter":::

Keyed injection of `IDurableValue<int>` with the key `"count"` and `stateManager.GetOrAddValue<int>("count")` return the same object within a manager, in either resolution order. Both construction paths use the manager's registry and configured format. Choose the convenience base when its helpers fit the application; constructor-injected composition gives existing grain hierarchies the same standard recovery behavior.

## Select a state type

| State type | Setup-time helper | In-memory API | Journaled operations |
| --- | --- | --- | --- |
| <xref:Orleans.Journaling.IDurableValue`1> | <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddValue*> | One mutable value | Set |
| <xref:Orleans.Journaling.IDurableDictionary`2> | <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddDictionary*> | <xref:System.Collections.Generic.IDictionary`2> | Set, remove, clear, snapshot |
| <xref:Orleans.Journaling.IDurableList`1> | <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddList*> | <xref:System.Collections.Generic.IList`1> plus `AddRange` | Add, insert, set, remove, clear, snapshot |
| <xref:Orleans.Journaling.IDurableQueue`1> | <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddQueue*> | Queue operations | Enqueue, dequeue, clear, snapshot |
| <xref:Orleans.Journaling.IDurableSet`1> | <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddSet*> | <xref:System.Collections.Generic.ISet`1> | Add, remove, clear, snapshot |
| <xref:Orleans.Journaling.IDurableTaskCompletionSource`1> | <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddTaskCompletionSource*> | Durable task completion | Complete, fault, or cancel |
| <xref:Orleans.Runtime.IPersistentState`1> | <xref:Orleans.Journaling.DurableStateManagerExtensions.GetOrAddPersistentState*> | Record-style state | Set or clear a versioned state value |

All named state components owned by a manager share its pending journal and write acknowledgement boundary. A caller's write can include changes staged by interleaved callers. Prepare fallible work before staging mutations and await `WriteStateAsync` before returning success. Cancelling the caller's wait leaves an already queued write running to its storage outcome. A failed journal operation fences the manager; a fresh activation recovers the durable outcome. See [Runtime behavior and consistency](runtime-behavior.md) for failure handling and safe staging.

Coordination with another grain or an external service requires an application protocol such as idempotency, an inbox, or an outbox.

An <xref:Orleans.Journaling.IDurableTaskCompletionSource`1> changes status in memory when `TrySetResult`, `TrySetException`, or `TrySetCanceled` succeeds. Its `Task` completes after a write acknowledges that status or recovery replays it, allowing waiters to observe a durable completion.

## Keep state names and schemas stable

The name supplied to the manager or keyed service identifies a durable state component across activations and deployments. Apply these rules:

- Keep each name unique within the grain.
- Preserve names when changing constructors or refactoring fields.
- Keep JSON key, value, and record schemas backward readable during rolling upgrades.
- Register every JSON payload type in the configured source-generated serializer context when trimming or using Native AOT.
- Retain removed state component definitions through the [retirement grace period](runtime-behavior.md#retire-a-named-state) when a rollback can reintroduce them.

Names use ordinal comparison. Repeated requests for a name and compatible application contract return the same instance. An incompatible contract or closed generic type for that name fails immediately. An unsupported application contract produces an explicit factory-registration error.

Declare all state components during construction or synchronous activation setup, before initialization begins. Use recovered state after initialization succeeds. Later `GetOrAddState` calls resolve existing components; a missing name fails immediately without changing the registry. Use <xref:Orleans.Journaling.IDurableStateManager.TryGetState*> for lookup without creation. Put runtime-varying keys inside a declared durable dictionary rather than creating a new named component for each key.

## Compose an activation-scoped feature

A reusable feature can own durable state independently of the grain's constructor and base class. Select the grain implementation once per grain type using <xref:Orleans.Metadata.GrainClassMap> in an <xref:Orleans.Runtime.IConfigureGrainTypeComponents> implementation, then register a shared setup action. This example selects the existing shopping-cart interface and records an activation count:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingSnippets.cs" id="journaled_feature":::

Register the feature as scoped and the configurator as singleton:

:::code language="csharp" source="../../snippets/compiled/Grains/JournalingSnippets.cs" id="journaled_feature_registration":::

The synchronous action resolves the feature from <xref:Orleans.Runtime.IGrainContext.ActivationServices> and enrolls its lifecycle participant. Resolving the feature's dependencies constructs the standard state manager if needed, and that manager enrolls itself before resolution returns. A setup action can be the first place an activation resolves its manager or state. The feature uses a stage after `SetupState` and before <xref:Orleans.Runtime.GrainLifecycleStage.Activate> so it increments recovered state before application activation begins.

Keep one enrollment owner per participant. Setup actions are shared across concurrent activations; resolve activation-specific data from the supplied context and keep shared callbacks stateless. See [Shared activation setup](../grain-lifecycle.md#shared-activation-setup) and [Journaling activation and recovery](runtime-behavior.md#activation-and-recovery) for ordering and failure behavior.

## Use journal-backed persistent state

Obtain journal-backed <xref:Orleans.Runtime.IPersistentState`1> through keyed injection or `GetOrAddPersistentState<T>(name)` during setup. Its familiar `State`, `WriteStateAsync`, and `ClearStateAsync` members write through the same journal manager as the durable collections. `ReadStateAsync` completes from the already-recovered in-memory state because activation setup replayed the grain journal.

Use a unique keyed service name exactly as you would for another durable state component. The `ETag` is the journal-backed state's recovered version and `RecordExists` indicates whether a stored value is present.

## Implement a custom journaled state

Define an application-facing state contract and an implementation of that contract and <xref:Orleans.Journaling.IStateMachine>. Register the mapping on <xref:Microsoft.Extensions.DependencyInjection.IServiceCollection> with <xref:Orleans.Journaling.JournalingHostingExtensions.AddStateMachine*> using the application contract and implementation as its two type arguments. Both types are reference types. In silo configuration, call `siloBuilder.AddJournaling()` for core setup and `siloBuilder.Services.AddStateMachine<TState, TImplementation>()` for the mapping. A storage-provider registration already performs the core setup. Application code obtains the state with `GetOrAddState<TState>(name)` during setup. The manager constructs, registers, and binds the implementation once, using its configured format and service lifetime.

The parameterless registration overload resolves the implementation's constructor dependencies from dependency injection. When construction needs the state name, use the factory overload: its callback receives the owning service provider and the requested state name and returns the implementation.

The state-machine protocol owns operation encoding, snapshots, replay, and volatile bookkeeping:

| Member | Responsibility |
| --- | --- |
| <xref:Orleans.Journaling.IStateMachine.Reset*> | Reset in-memory state and bind the supplied journal stream writer. |
| <xref:Orleans.Journaling.IStateMachine.ReplayEntry*> | Apply a recorded operation during recovery. |
| <xref:Orleans.Journaling.IStateMachine.WritePendingEntries*> | Emit pending operations into the supplied writer. |
| <xref:Orleans.Journaling.IStateMachine.WriteSnapshot*> | Emit the state needed to reconstruct the current contents. |
| <xref:Orleans.Journaling.IStateMachine.OnRecoveryCompleted*> | Finish reconstruction before application use. |
| <xref:Orleans.Journaling.IStateMachine.OnWriteCompleted*> | Publish effects which depend on storage acknowledgement. |

An implementation runs on one logical grain thread. Recovery uses fresh instances and replay. After a journal operation fails, the manager remains fenced and its owner creates a new manager and state instances.

## Own a standalone journal

<xref:Orleans.Journaling.IJournaledStateManager> extends `IDurableStateManager` with the advanced ownership operations: <xref:Orleans.Journaling.IJournaledStateManager.RegisterStateMachine*>, <xref:Orleans.Journaling.IJournaledStateManager.InitializeAsync*>, whole-journal <xref:Orleans.Journaling.IJournaledStateManager.DeleteStateAsync*>, asynchronous disposal, and <xref:Orleans.Journaling.IJournaledStateManager.PendingWriteByteCount> diagnostics.

Use <xref:Orleans.Journaling.IJournaledStateManagerFactory.CreateStandalone*> when an integration owns a journal independently of a grain activation. Declare its state components before initialization, await recovery, and dispose the manager when its work ends:

:::code language="csharp" source="./snippets/journaling/JournalingBasics.cs" id="standalone_durable_state":::

The factory's selected provider and the manager's configured format apply to all state components it creates. Each standalone manager owns its registry and lifetime. `CreateStandalone` leaves its DI scope unallocated. The first DI-created state component, such as the value in this example, creates one manager-owned scope. The manager binds that scope to itself, reuses it for later state-service resolutions, and disposes it with the manager.

Directly registered state machines and existing-state lookups use their supplied instances. Initialization and writes using already-supplied same-format codecs remain scope-free; replay access to <xref:Orleans.Journaling.JournalReplayContext.ServiceProvider> creates the scope when services are needed. This supports integrations such as Durable Jobs shards which supply their own state. Grain-owned managers use the existing activation scope and receive initialization and disposal from the activation lifecycle.
