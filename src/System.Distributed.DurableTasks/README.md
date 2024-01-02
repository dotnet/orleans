# TODO

* [ ] Implement Azure Table Storage support for grains
* [ ] Enhance reminders to poll/trigger pending workflows
  * [ ] Also improve reminder scalability
* [ ] Implement Cosmos DB Document storage for grains (so we have multiple models)
* [ ] Add samples for common Workflow patterns
    * [ ] Human-in-the-loop
    * [ ] Subscription (repeated)
        * [ ] Large delay
        * Include cancellation
    * [ ] Provisioning (eg, Azure infra)
    * [ ] Fan-out/fan-in
    * [ ] ETL/Batch
    * [ ] Saga (show the unwinding pattern)

### Overview

* *Durable tasks* allow you to express reliable, long-running processes using Orleans.
* Grain methods can return `DurableTask` and `DurableTask<T>` methods in addition to the non-durable variants such as `Task` and `Task<T>`.
* `DurableTask` methods are different from their non-durable counterparts in several ways:
  * For the caller:
    * Callers can durably *schedule* invocations of these methods, allowing them to receive acknowledgement once a method call is scheduled rather than only knowing once a method call has completed.
    * Callers can identify invocations of a method using a user-defined identifier string.
    * Callers can poll for completion of the invocation and can retrieve the results of the invocation using the invocation's identifier.
  * For the implementation:
    * Durable task invocations have persistent state which can be used to save and restore their execution progress.
      * This state is expressed as a collection of key-value pairs where the keys are strings and the values are any value.
    * Durable tasks can be expressed as a set of steps and the result of individual steps can be stored and restored using the above mentioned state.
    * Durable task invocations can be retried after a failure. When a durable task is retried, it is executed again from the beginning and the implementation can examine its persistent state to resume execution from the first incomplete step.

* Workflows are methods on grains which return a value of type `DurableTask` or `DurableTask<T>`.
* Methods which describe workflows are called workflows.
* When a request is made to schedule a workflow for execution, that is called a workflow *invocation*.
* Each workflow invocation has a unique identifier which can be specified at scheduling time.
* Each time a workflow is *executed*, that is called a workflow *run*.
  * Each *run* has a unique identifier.
* Workflows are logically a sequence of steps to be executed
  * Each step is uniquely named by the developer
* Each workflow *run* involves executing the workflow method from the beginning
  * Workflow methods use their private state to determine the last completed step and continue from there.
* Workflow invocations have access to private, persistent key-value storage.
* Workflows can access and update the state of the grain which defines them.
* Updates to grain state and workflow state are guaranteed to occur atomically
  * If the developer specifies that both are to be updated simultaneously, there is no possibility for only one or the other to be updated.
* 

* A grain is a unit of atomicity: developers should be able to read & modify the state of a grain and the state of a workflow atomically. This includes:
  * Enqueuing requests to be sent (outbox)
  * Modifying grain state and the state of components of the grain (scheduling tasks)
  * Marking incoming requests as handled


```csharp
class UserGrain : Grain, IUserGrain
{
  IStateManager _state;
  IPersistentState<UserState> _userState; // Assume these are injected in via the constructor.
  IDurableTaskManager _taskManager;
  ITransactionClient _transactionClient;

  public async DurableTask SoftDelete()
  {
    if (_userState.Value is null) return;
    
    _userState.Value.IsSoftDelete = true;

    // Schedule hard deletion.
    // Since we've specified a task id, if the task exists already, it will not be re-scheduled.
    await _taskManager.ScheduleAsync<IUserGrain>(
      id: "delete",
      due: TimeSpan.FromDays(30),
      task: self => self.HardDelete());
  }

  public async DurableTask Undelete()
  {
    if (_userState.Value is null) return;

    // Step is transactional.
    // Same behavior as .AsStep()?
    // Problem: It's not clear why Step(...) is needed here instead of just calling the body directly.
    await Step("undelete", async () =>
    {
      _userState.Value.IsSoftDeleted = false;
      await _taskManager.CancelTaskAsync("delete");
    });

// Using the Step API
    var id = await Step("id", () => Guid.NewGuid().ToString("N"));
    await Step("issuePayment", () => _paymentGateway.CreatePaymentAsync(id, details));
    
// Using the .AsStep() API
// Benefits: looks cleaner. Can clear up intermediate state ("id") once it's no longer needed, easier to nest.
    await IssuePayment(orderDetails).AsStep("issuePayment");

// Using the .AsTransactionalStep API.
    await PerformTransfer(from, to, amount).AsTransactionalStep("performTransfer");
  }

  private async DurableTask IssuePayment(OrderDetails details)
  {
    var id = DurableTaskContext.GetOrAdd("id", () => Guid.NewGuid().ToString("N"));
    await _paymentGateway.CreatePaymentAsync(id, details);
  }

  private async DurableTask PerformTransfer(IAccountGrain from, IAccountGrain to, int amount)
  {
    await myTransactionalState.PerformUpdate(state => state.TotalTransferred += amount);
    await from.WithdrawAsync(amount);
    await to.DepositAsync(amount);
  }

  public async DurableTask HardDelete()
  {
    _userState.Value = null;
    // Value is implicitly saved upon exiting the method.
  }
}
```

* A `DurableTask` method can:
  * Access state via `DurableTaskContext.Current`
  * Update grain state

### Lifecycle of a durable task

* Initial scheduling: client calling a durable task method on a grain
  * 4 scheduling alternatives (with/without specified id, with/without specified options):
     * `await grain.MyDurableTaskMethod().ScheduleAsync();`
       * Schedules a durable task method with no id (no deduplication).
       * This might never complete, just like any other method, but exacerbated because durable tasks are generally long-running.
     * `await grain.MyDurableTaskMethod().ScheduleAsync("my-task-id");`
       * Schedules a durable task method with an id.
       * If a task was previously scheduled with this id and has not been cleaned up, this is equivalent to no operation.
       * If the task has not been scheduled, this will schedule it and the 
     * `await grain.MyDurableTaskMethod().ScheduleAsync("my-task-id", options);`
       * Schedules a durable task method with a specified id and options.
       * If a task was previously scheduled with this id and has not been cleaned up, this is equivalent to no operation.
         * This means that the options will be ignored if the task has already been scheduled.
     * `await grain.MyDurableTaskMethod().ScheduleAsync(options);`
       * Schedules a durable task method with a random id and options.
* Kinds of `DurableTask`/`DurableTask<T>`
  * Broadly speaking, 2 kinds:
    * `DurableTask` instances returned from a *grain call*
      * Implemented internally by `DurableTaskRequest`/`DurableTaskRequest<T>` (TBD)
      * `ScheduleAsync(...)` schedules the task on the remote grain
    * (async) `DurableTask` methods
      * Implemented internally by `DurableTaskMethodInvocation`/`DurableTaskMethodInvocation<T>`
      * Compiler uses `DurableTaskMethodBuilder`, which returns the implementation type.
      * `ScheduleAsync(...)` is not allowed?
        * Could it schedule the callback in the context of the current workflow?
        * `DurableTask.Delay(TimeSpan).AsStep("foo")` and `DurableTask.DelayUntil(DateTimeOffset).AsStep("bar")`


Thoughts:
* We should keep the semantics of `DurableTaskMethodBuilder` which result in it not executing the method immediately, but instead returning the instance, since that allows us to:
  * Configure execution, allowing us to provide context to the execution (such as a TaskId which can be sent along with requests for idempotency); and
  * Prevent execution when the method has previously completed, returning the result immediately instead.
* We should remove `SchedulingOptions` (retries, delayed scheduling, etc) from `DurableTask` APIs and keep things simpler:
  * You have a task and can configure it
  * Why? Because it seems that we cannot apply the semantics everywhere:
    * Retries, specifically are difficult with method calls, because (I think) we would need to create an in-memory copy of the state machine.
    * Delayed scheduling are ok, but delays are well served by `await Task.Delay(...)` and delayed scheduling is usually meant for delays in the order of minutes to years, not seconds, so they may not be 
* Outside of a `DurableTask` method, should awaiting the result of a `DurableTask` be blocked?
  * Allowing it may result in unresponsive programs
  * It may be too easy to accidentally do.
* Instead, should we allow long-polling? `(ScheduledTask).WaitAsync(TimeSpan)` which returns some value (not a `TimeoutException`!) if the task did not complete in time? At least this is very clear as to the semantics and it's clear that the method may be unresponsive for the specified amount of time.

Flow:
* Call a DurableTask method on a grain:
  * Internally, the generated proxy constructs a `DurableTaskGrainMethodInvocation` instance and returns it.
    * This is a special implementation of `DurableTask` which synchronously captures the `IInvokable` which represents the grain call but does not submit it to the runtime immediately.
    * Instead, it allows `DurableTask` extension methods to be invoked first, to configure the `DurableTask`/`IInvokable` 
  * Upon awaiting the `DurableTask`, the corresponding `IInvokable` is submitted to the runtime (along with its configuration)
    * Note: Why not use the regular `DurableTaskMethodInvocation` implementation? Because, if we did, it would not synchronously copy the method arguments, so they might mutate by the time the task is submitted. Maybe this is fine, but it's better to be safe than sorry.
  * The generated `IInvokable` uses a base class: `DurableTaskRequestBase`, which is responsible for invoking the request on the target grain instance.
  * `DurableTaskRequestBase` has a property to hold the configured `ScheduledTaskContext` (rename?) which holds information such as the scheduled task's *id* and other options.
  * `DurableTaskRequestBase.Invoke()` sets the context via an `AsyncLocal`, `ScheduledTaskContext.Current` and calls and awaits the grain method.
  * The awaiter for the method sees the context (task id) and uses it to fetch any previous state....


* Schedule a `DurableTask` method on a grain:
  * The caller calls a `DurableTask`-returning method.
  * The caller calls and awaits `.ScheduleAsync()` to schedule the call.
  * A `ScheduledTask` is returned.
  * The caller polls the resulting `ScheduledTask` in a loop using the `.WaitAsync(TimeSpan)` method.
* Calling `ScheduleAsync()` on a durable task invokes a grain extension method on the grain:
  * `IDurableTaskManagerGrainExtension.ScheduleAsync(ScheduledTaskId id, IInvokable task)`
* The call returns once the task has been scheduled durably (not once it has executed to completion).
* Calls to `WaitAsync(TimeSpan)` are implemented via the same grain extension, but a different method:
  * `IDurableTaskManagerGrainExtension.WaitAsync(ScheduledTaskId id, TimeSpan maximumWaitDuration)`
* `IDurableTaskManagerGrainExtension` is responsible for invoking tasks on the grain itself.
* Once a task has completed, its result is stored so that calls to `WaitAsync(...)` can propagate the result back to the caller.
* To prevent storage costs from growing unbounded due to many task results which are no longer needed by the application, these values will expire after some configurable period of time.
  * These values can be more eagerly cleaned up in the case that a task result is known to not be needed outside of some scope. For example, if the task is called as a step within another task, its result can be discarded once the caller has acknowledged the result.
  * This is facilitated by a `IDurableTaskManagerGrainExtension.RemoveAsync(ScheduledTaskId id)` method call, called implicitly by the caller after it has made the results of the `ScheduledTask` call durable from within a `.AsStep(id)` call.

* Handling loops
  * When using the step-based programming model (where workflows are expressed as `DurableTask`-returning methods on `Grains` which are defined as a partially-ordered set of named steps), there must be some way to identify iterations of a loop, so that behavior matches what a user might reasonably expect.
    * With no knowledge of loops and no care taken by a user, the loop body will be executed multiple times, invoking tasks with the same identifier.
    * These tasks, upon being invoked for a subsequent time, will either be skipped (since a task with that id has already completed), or they will throw an error (since a task with that id has already been started during this attempt), depending on what we decide the semantics should be.
  * Perhaps we should have some method for entering a new scope for a loop iteration.
    * How would we restore the loop variable to the correct state?
    * If we don't, then an unbounded number of loop iterations could cause an unbounded storage growth for the `DurableTask`-returning method.
  * `IDisposable DurableTask.UsingLoopIteration(object loopVariable)`
    * Creates a new scope, just as a *step* creates a new scope.
  * It does not seem ergonomic and it would probably not be clear to a user why they should use it.

* Persistence and atomicity within durable tasks
  * It is natural for a durable task on a grain to affect the state of that grain
  * It would be difficult for developers to reason about the correctness of a durable task method if updates to different components of the grain did not proceed as atomic steps.
  * Since durable task methods are intended to proceed in steps, perhaps it makes good sense for updates to the state of a grain to proceed in atomic steps.
  * To do this, we can implicitly hold the state changes made to a grain in-memory for the duration of a DurableTask method and only apply them once the method exits.
  * Should these state changes be made to a copy of the grain's state? If the `DurableTask` invocation fails, operating on a copy makes it easier to roll-back the changes, which is something that is required for atomicity in the ACID sense.
    * It is possible to make reasonably understandable systems without this guarantee, but it is difficult.
    * Without operating on a copy, the grain's state could be re-sync'd from storage after an exception escapes a `DurableTask` method.
  * An alternative is to have an `UpdateAsync` method which accepts an async delegate which updates multiple components within the grain atomically.
  * Another alternative is to have no effects become persisted until a `WriteStateAsync` call. This leaves the developer to clean up in-memory changes.
  * If we disallow in-memory mutations, then changes can be queued up and applied or discarded as needed without necessarily needing to take a copy of the underlying data.
  * Note: Orleans.Transactions takes a full copy today
  * Given we do not expect to need to roll-back often, perhaps re-syncing from storage is best.
  * **DECISION** Reload state from storage upon failure to prevent needing to take an in-memory copy for each operation.

* Transactional steps
  * Without transactional steps, side effects of operations may occur multiple times, affecting the correctness of the durable task.
  * When an operation crosses grain boundaries, a transaction can be used and the durable task state can be enlisted in the transaction.
  ```csharp
  IPersistentState<int> _balance;

  DurableTask MyMethod(IAccountGrain otherAccount)
  {
    // The transfer will be performed exactly once
    await DurableTaskContext.RunTransactionalStep(
      "transfer",
      () => otherAccount.Withdraw(100));
  }
  ```

  * The `DurableTask` methods which allow specifying a task id modify the `DurableTaskGrainMethodInvocation` instance so that when it is submitted to the runtime, the id, etc, are carried with it.
  * When awaited, the `IInvokable` is submitted to the runtime to send to the remote grain.
  * The 


Questions:
* Should workflows be expressed as methods on state*ful* objects, or as functions on state*less* objects?
  * Methods on stateful objects (grains):
    * Allow for more complex interaction patterns, rather than just a single invocation
        * Eg, consider cancellation or modification of the workflow instructions after the workflow is initiated.
        * For example, a monthly subscription renewal workflow has the subscription details changed.
    * Give the workflow context and allow it to potentially atomically update the state of the object
    * If a workflow is long-running, then the workflow execution might prevent other requests from running
  * Functions on stateless objects:
    * Are conceptually simpler
    * Need some external mechanism for signaling and querying the workflow (eg, see Temporal and Azure Durable Functions, which use this approach.)

---
Characteristics of a good solution:

* Transactions for performing atomic operations which cross entity boundaries
* A facility to schedule work to be performed in the future
  * Including repeated work
* Ability to atomically update state of the workflow and the entity which it is attached to simultaneously


TODO:

* An API for rescheduling a task which is currently running.
  * Do we require that each invocation has a unique name? That sounds reasonable.
  * In that case, the invocation id can be stored in the grain's state so that methods can be created to cancel the task and check its progress, for example, and to ensure singleton scheduling.
  * In this case, perhaps `ScheduleAsync` should throw or return a status (instead of a ScheduledTask?) so that we can determine when a durable task has already been scheduled before.
    * Currently, it is idempotent, *get or schedule*, which seems more appropriate than making it throw.
* An API/model for atomically updating grain state, cancelling & scheduling tasks on the grain, and scheduling calls to other grains (i.e, outbox pattern), etc.
    * Should all APIs which manipulate a grain (state, tasks, outbox, etc) be synchronous with a single `WriteStateAsync()` API to commit the updates to storage?
* Add `GetTask(DurableTaskId)` API to grain/etc to get a task with a given name
* Add `GetTasks()` API to grain to list tasks. Do we need separate APIs for active vs completed tasks?
* Should `ScheduleAsync()`, etc, support Orleans Transactions?
  * How should transactions work with this in general?
* How can external services integrate with this pattern?
  * Send "idempotency-key" to external service
* How can we support the idempotency-key pattern so that we can create Web services which others can integrate with?
  * Schedule a DurableTask with the specified 

Stepwise Task APIs for use within `DurableTask` methods:
* `await (DurableTask).AsStepAsync("foo")` - invoke some durable task as a named step in the workflow, skipping the invocation if a step with that name has already been completed and had its result (if any) stored.
* `await DurableTaskContext.CurrentTask.RunStepAsync("foo", async () => {...})` - invoke some (async) lambda as a durable step, skipping it if it has already been completed and its result (if any) stored.
* `await (DurableTask).AsTransactionalStep("foo")` - as above, but creates a transaction scope and runs the step and updates the task state transactionally. This is useful for cases where one or more grains are being called. It provides atomicity for cross-grain calls.
  * Question: why not always make steps transactional? Is it just practical concerns? Could developers get themselves into trouble?
  * Question: are there things which a user cannot do within a transaction (transactionally), such as write to a stream?
    * If so, how do we make it clear to a user that some actions are not transactional despite being executed within a transactional scope?

* Separate orchestration from actions?
  * This is what Temporal and Azure Durable Functions do.
  * Downsides:
    * Added ceremony, since splitting side-effects from coordination requires some boundary (eg, a separate class/function)
      * But there needs to be some way to demarcate idempotent steps anyway. In the above proposal, it's usually `SomeThing(foo).AsStep("foo-step")`
  * Upsides:
    * ?


* Create a durable task from a delegate:
  * APIs:
    * `DurableTask DurableTask.Run(Action)`
    * `DurableTask DurableTask.Run(Func<Task>)`
    * `DurableTask<T> DurableTask.Run(Func<Task<T>>)`
    * `DurableTask<T> DurableTask.Run(Func<T>)`
  * Usage:
    ```csharp
    var id = await DurableTask.Run(() => Guid.NewGuid()).AsStep("generate-id")` 
    ```
    Equivalent to:
    ``` csharp
    var id = await GenerateId().AsStep("generate-id");

    async DurableTask<Guid> GenerateId() => Guid.NewGuid();
    ```
* Create a durable task from a value:
  * `DurableTask<T> DurableTask.FromResult<T>(T value)`

* Deduplicate invocation of a durable task from within another durable task, returning the result of invocation:
  * Overview:
    * Durable tasks may be arbitrarily long-running. To help ensure that tasks make progress despite the presence of system crashes, we have a mechanism for storing progress. Progress can be stored for individual *steps* which make up the task. Each *step* is a task itself, so these steps naturally form a tree. When recovering from a crash, we need to do our best to restore the state of a previous execution so that the task can continue from some point and make progress. The **Procedure** section describes how this process works.
  * Procedure:
    * Associate an identifier (*task id*) with each task (*step*) using APIs defined below.
    * Check the current, in-scope *durable task context*'s step storage to see if this task has already completed.
      * If so, **return** the previously stored result.
      * If there is an existing context but it has not been marked as complete, **continue**.
      * If there is no existing context, create a new *durable task context* for the task and add it to the current context using the defined *task id*.
    * Make the task context the current context.
    * Invoke the task.
    * Set the result of the task and mark the context as complete.
    * **Return** the result of the task to the caller.
  * APIs
    * `DurableTaskStep AsStep(this DurableTask task, string taskId)` (extension method)
    * `DurableTaskStep<T> AsStep<T>(this DurableTask<T> task, string taskId)` (extension method)
  * Usage:
    ``` csharp
    var callback = await DurableTaskCompletionSource.Create<Response>().AsStep("payment-task");
    var paymentRequest = StripeClient.SubmitChargeRequestAsync(charge, idempotencyKey: callback.Id);
    var paymentResult = await callback.Task;
    ```

  * `DurableTaskCompletionSource` (DTCS)
    * Overview:
      * When integrating with external systems, 
      * `DurableTaskCompletionSource`/`DurableTaskCompletionSource<T>` instances can be used within grains to create globally identifiable, named, distributed, fault-tolerant, awaitable tasks.
    * Uses:
      * To implement the *idempotency key* pattern, a new DTCS can be created with either an automatically generated identifier (Eg, `Guid.NewGuid().ToString("N")`) or a specified identifier.
      * DTCS identity is global: any caller can get a reference to a DTCS by its identifier.
    * Implementation:
    * APIs
      * Key members on class:
      ```csharp
      public class DurableTaskCompletionSource<T>
      {
        DurableTask<T> Task { get; }
        DurableTaskId Id { get; } // Should this be a property on DurableTask? Should it be a string?
        ValueTask SetResultAsync(T value);
        ValueTask<bool> TrySetResultAsync(T value);
        ValueTask SetExceptionAsync(Exception exception);
        ValueTask<bool> TrySetExceptionAsync(Exception exception);
        ValueTask SetCanceledAsync();
        ValueTask<bool> TrySetCanceledAsync();
        ValueTask<(bool IsCompleted, T Result, > IsCompletedAsync();
      }

      public class DurableTaskCompletionSource
      {
        static DurableTaskCompletionSource<T> Create<T>(); // The DurableTaskCompletionSource
        static DurableTaskCompletionSource Create(); // The DurableTaskCompletionSource
        static DurableTaskCompletionSource<T> Get<T>(string id);
        static DurableTaskCompletionSource Get(string id);

        DurableTask Task { get; }
        DurableTaskId Id { get; }
        ValueTask SetResultAsync();
        ValueTask<bool> TrySetResultAsync();
        ValueTask SetExceptionAsync(Exception exception);
        ValueTask<bool> TrySetExceptionAsync(Exception exception);
        ValueTask SetCanceledAsync();
        ValueTask<bool> TrySetCanceledAsync();
        ValueTask<bool> IsCompletedAsync();
      }
      ```
    * Usage:
      * Using a `DurableTaskCompletionSource<T>` from an ASP.NET Controller:
      ```csharp
      // In an ASP.NET Controller method:
      var completion = DurableTaskCompletionSource.Create<MyResponse>(idempotencyId);
      
      // Optimization: if the task has already been completed, there is no need to issue the operation again.
      var (isCompleted, result) await completion.Task.TryGetResultAsync();
      if (isCompleted) 
      {
        return result;
      }

      // Issue the request, passing the completion
      await grain.PerformWork(request, completion);

      // Wait for the operation to complete, with a cancellation to timeout the request if it takes too long.
      return await completion.Task.WaitAsync(cancellationToken);
      ```
    * Notes:
      * `DurableTaskCompletionSource` instances can be serialized and sent across the wire. Since these instances are globally-addressable, each instance is a proxy. This allows instances to be sent to other instances, for example as a kind of callback mechanism for long-polling.
    * Open Questions:
      * How should storage for DTCS work? Should the default DTCS id point to the grain which created it and use it for storage?
        * DTCS could be implemented as a grain extension and there could be two main cases:
          * DTCS created with an explicit identifier
            * This would be a global DTCS and would implicitly be 
          * DTCS created with no explicit identifier
            * If the DTCS is created within a grain, the DTCS.Id would point to the grain, eg:
              * `grain:` + *GrainId* + `:` + *RandomId*
      * Does DTCS support transactions?
        * I say, yes: you can transactionally create or complete a DTCS
        * In practice, this currently means that the DTCS state must be transactional.
      * Does DTCS support metadata?
        * We should probably support associating K/V metadata with a DTCS
          * Motivation: the stripe API stores the incoming *request* as well as the response. This allows validating the request before returning the response. It helps them to catch programming errors (accidentally reusing an `idempotencyKey` within a 24h window)
      * Do DTCS values expire?
        * Yes, but how? Stripe API keeps values around for 24h.
        * If DTCS is stored on grain state, could lazily expire them.
        * If DTCS is stored independently (eg, in dedicated storage), we could periodically scan storage, similar to reminders, and expire completed ones after some configurable period of time (similar to defunct silo membership table cleanup)
      * Would there be a need to have different expiry policies for different DTCS? What about different storage depending on the type, or should there be one global DTCS store?
      * Should there be an easy way to create a DTCS for any `DurableTask<T>`?
        * Eg, imagine an extension method `task.AsWorkflow(id)`
          * This is similar to `task.AsStep(id)` *within* a workflow, but *AsWorkflow* signifies that this is not a step but rather a standalone workflow and therefore the `id` is global.
        * Under the covers, this will set a `ReplyTo:` address, the address of a `DurableTaskCompletionSource<T>` backing grain. Most likely, we should make this `ReplyTo` a data structure which can contain both a grain/service address and a *task id*. This will allows flexibility.
        * The caller can first check whether the task has been completed and only complete it if it hasn't already.
        * This leaves the door open for transactional workflows and transactional steps.

---

# (DurableTask vs Workflow vs WorkflowStep is a good distinction, so lets try again)

---

# Workflow programming in Orleans

* Durable tasks are a building block for reliable, long-running operations, commonly known as *workflows*.
* Workflows are expressed using grain methods with a return type of `DurableTask` or `DurableTask<T>`.
* Workflows separate scheduling from invocation. This is useful for long-running operations because invocation of a workflow can often take an arbitrarily long period of time, such as multiple days and applications typically want to return control back to the user as soon as such an operation has been scheduled, rather than waiting until the operation has completed.
* Once a workflow has been scheduled, the system is responsible for ensuring that it is *eventually* executed to completion. This promise must hold even in the presence of failures, such as temporary power outages, network faults, and system restarts.
* Each workflow is given a unique identifier at creation time. This identifier can be provided by the application or generated automatically.
* The status and result of a workflow can be queried using the workflow's identifier.
* Workflows consist of one or more *workflow steps*. The `DurableTask` which defines the workflow is also a workflow step and each step can itself be composed from multiple other workflow steps. Therefore, every workflow step except for the top one has a parent and potentially multiple children.
* Each *workflow step* has an identifier which is unique within the context of the parent step.
* When a workflow step completes, its result is stored, allowing future executions of the workflow to see the result and skip invocation of the step.
* If the execution of a workflow encounters a step which has already been completed by a previous execution of that workflow, the step is not executed again. Instead, the stored result from the previous execution is returned so that the workflow can continue. This allows the workflow to continue to make progress when there are faults which occur part-way through an execution.
* Workflow steps can involve loops and other constructs. In these cases, *step state* allows variables, such as loop variables, to be persisted so that progress can resume from the a recent iteration instead of always starting the loop from the beginning. This requires some care on the part of the application developer since the method which defines the step must be able to recover to a consistent point using that state when it is restarted after a fault.

## Workflows
  * Created from an instance of `DurableTask`/`DurableTask<T>` using the `task.AsWorkflow(id)` extension method.
  * Separate scheduling and invocation/completion: callers can receive a notification when the workflow is durably scheduled and can receive a subsequent notification when the workflow has completed.
    * `workflow.ScheduleAsync()` durably schedules a workflow for immediate invocation.
    * `workflow.ScheduleAsync(DateTimeOffset)` and `workflow.ScheduleAsync(TimeSpan)` schedule the workflow for execution at a later time.
## Workflow steps
  * Created from an instance of `DurableTask`/`DurableTask<T>` using the `task.AsStep(id)` extension method.
  * Can only be created from within the context of a workflow.
  * Have identities which are unique with respect to their parent.

## `DurableTask` and `DurableTask<T>`

## `DurableTaskCompletionSource` and `DurableTaskCompletionSource<T>`

## Workflow Step State
  * For more advanced scenarios, such as loops
  * Allows access to persistent key-value state which is scoped to the current step

```csharp
// Create a workflow from a durable task
public static Workflow AsWorkflow(this DurableTask task, WorkflowId id);
public static Workflow<T> AsWorkflow(this DurableTask<T> task, WorkflowId id);

// Create a workflow step from a durable task
public static WorkflowStep AsWorkflowStep(this DurableTask task, WorkflowStepId id);
public static WorkflowStep<T> AsWorkflowStep<T>(this DurableTask<T> task, WorkflowStepId id);
```

## Creating reliable workflows using Orleans Durable Tasks

1. Express your workflow using grain methods which return `DurableTask` & `DurableTask<T>`
```csharp
public interface IMyTeamGrain : IGrain
{
 DurableTask<JoinTeamResponse> CreateJoinTeamRequest(JoinTeamRequest request);
}

... // TODO: more code, eg implementation
```

* Workflows
* Workflow *Steps*
* DurableTaskCompletionSource<T>

* A workflow step is a sub-workflow. Should we use `AsSubworkflow(...)` as the extension method? `AsNestedWorkflow(...)`

* Calling `AsWorkflowStep` on a `DurableTask<T>` creates a `WorkflowStep<T>` which hold a `DurableTaskContext<T>`.
* `DurableTaskContext<T>` has a `TaskCompletionSource<T>` within it.
* `IGrainContext` exposes `IDurableTaskGrainRuntime` which contains the collection of active `DurableTaskExecutionContext` instances, allowing querying, management, and completion of durable tasks by remote callers.
* `DurableTaskRequest`, the base class for requests made to `DurableTask`-returning methods, includes a `DurableTaskRequestContext`.
* `DurableTaskRequestContext` includes:
  * `TaskId` — the fully-qualified identifier for the request.
  * `SchedulingOptions` — which include retry options and scheduling delays.
  * `CallerId` - the identity of the caller to send the response to.
  * `RequestContext` - a `Dictionary<string, object>` for other properties
* `DurableTaskRequest.Invoke`, the method called on the target instance when a request is issued, fetches the `IDurableTaskGrainRuntime` component from the `IGrainContext` using the `GetComponent<T>()` method and calls `ScheduleRequestAsync(DurableTaskRequest)` to ensure that the request is durably scheduled. Once it has been scheduled, a `DurableTaskResponse` is returned to the caller to indicate that it has been scheduled or, if the task has already been completed, the result of invocation.
* `IDurableTaskGrainRuntime`