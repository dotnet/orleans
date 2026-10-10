# Durable Messaging: stock reservations

A self-contained .NET 10 console host runs one localhost Orleans silo and two
application grains. An **order** submits a typed reservation under an application
command ID; a **stock** grain updates inventory and sends a deterministic reply.
Resubmitting that same ID produces `Duplicate` admission and one stock decrement.
Volatile journals and in-memory jobs keep the demonstration self-contained.

## Run

**Build with locally packed Orleans sources until these APIs are published.**
The declared versions follow the repository samples' approved package baseline.
The repository build substitutes the current source packages from its local feed.

From the repository root:

```powershell
pwsh .\samples\Build-Samples.ps1 -SkipExternalAssets
# Or run Validate-Samples.ps1 to perform policy checks and the complete build.
dotnet run --project .\samples\DurableMessaging\DurableMessaging.csproj --configuration Release --no-build
```

`Build-Samples.ps1` packs current Orleans sources with a unique version and passes
`OrleansSamplePackageVersion` and `OrleansSamplePackageSource` to the samples
solution. The sample's NuGet references exercise the same package boundary as
application projects.

### Copying the sample out

`DurableMessaging/` is the entire copy-out unit, including its own
`Directory.Packages.props`. Once these APIs are published, align its Orleans
package versions to that release and run:

```powershell
dotnet run --project .\DurableMessaging.csproj --configuration Release
```

Until then, standalone builds have an explicit unpublished-package exception.
To test a copy outside this repository today, first pack Orleans using
`Build-Samples.ps1`, then copy the sample folder and replace **only the copy's**
central Orleans package versions with that local feed's generated version.
Restore/build with the local feed and the normal public dependency feed:

```powershell
dotnet restore .\DurableMessaging.csproj --source <absolute-local-package-feed> --source https://api.nuget.org/v3/index.json
dotnet run --project .\DurableMessaging.csproj --configuration Release --no-restore
```

For a feed with separate stable and experimental versions, use the stable version
for Server/Sdk and the experimental version for DurableJobs/DurableMessaging/
Journaling. All transitive Orleans dependencies must come from the same source
build. The repository's `samples/Directory.Build.targets` overrides are not part
of the copy-out unit, so passing `OrleansSamplePackageVersion` alone to the copy
will not replace its declared versions.

## What to review

1. The host initializes ten units of `trail-shoes`, then submits
   `orders/order-1042/reserve-stock` for two units through the order's outbox.
   Once the original reply is journal-acknowledged, it reconstructs the same command
   and calls explicit inbox admission. The second submission returns `Duplicate`.
2. `ReserveStock`, `Restock`, and `ReservationOutcome` have keyed `DurableMessageType<T>`
   bindings under `inventory.reserve.v1`, `inventory.restock.v1`, and
   `inventory.reservation-result.v1`.
   Each envelope carries its hierarchical command ID, exact subject, sender,
   receiver, and owning `ArcBuffer` payload.
3. `inbox.RegisterHandlers` installs one subject dispatcher per grain. Stock
   registers separate typed reservation and restocking methods; the order registers
   its typed outcome method. Each registration supplies the grain as state and a
   static delegate receiving the decoded record, grain, and inbox context.
   The primary-constructor grains register routes and the order's receipt hook in
   `OnActivateAsync`. Journal recovery restores their durable state first; requests
   and queued inbox pump turns begin after activation completes.
   Configuration freezes the routes before processing; synchronous dispatch checks
   the attempt token before entering the method.
   The console run exercises reservation and outcome subjects; `Restock` supplies
   the additional positive-stock-increment protocol for the same stock inbox.
   Typed outbox `Send` and `SendReply` rent internal pooled encoders, stage the
   message, and dispose their temporary envelopes after retaining the outbox's pin.
   The explicit duplicate-admission call uses `DurableMessageType<T>.Create` and
   a local `using` owner. Handlers borrow inbox envelopes through actual method
   completion.
4. The stock handler runs once. Its inbox completion fact recognizes the same
   command ID across senders and subjects during retention. The result reply uses
   the deterministic child `orders/order-1042/reserve-stock/result`; inventory
   stores the remaining stock, accepted reservation count, and execution count.
   One ID binds an immutable command, including quantity, destination, and subject.
5. The dispatcher decodes and checks cancellation at its boundary. Each typed
   handler validates and computes all results locally. Stock calls `SendReply`
   before inventory mutation; the helper encodes before outgoing staging.
   From that first staging through inventory assignment, `context.Complete()`,
   and method return, execution is synchronous.
   Inventory, outgoing intent, and inbox completion share the journal
   boundary. The runtime owns the subsequent write and acknowledgement.
6. The order's ordinary submit method awaits `WriteStateAsync` to commit its
   outgoing intent. To observe the completed round trip, the host awaits a
   `TaskCompletionSource` observer released
   by the order's journal **after-acknowledgement hook**. The hook reports a snapshot
   taken before capture. The observer is host-local demo instrumentation;
   the durable reply is the outcome in the order grain's journaled receipts.

A successful run prints:

```text
ACKNOWLEDGED: original reply orders/order-1042/reserve-stock/result
RESUBMITTED: orders/order-1042/reserve-stock, admission=Duplicate
VERIFIED: remaining stock=8, reservations=1, processed requests=1.
```

The assertions verify the original correlated outcome, duplicate admission,
exactly one handler execution, and exactly one business effect. A failed assertion or the two-minute deadline
fails the process with a nonzero exit code. The host stops after the verification.
Only one localhost sample using the default silo/gateway ports should run at a time.

## Storage and production boundaries

`UseInMemoryDurableJobs` and `AddVolatileJournalStorage` keep this demonstration
standalone. Their acknowledgements exercise the real journaling, inbox, outbox,
and job pumps. **Stopping the process discards its state.** Configure persistent
journal and job providers to recover across process restarts.

Retain inbox completion records for the full supported resubmission horizon.
After expiry, the same command ID can be admitted and executed again. A completed
duplicate acknowledges the retained ID/time fact; callers obtain business results
from the original durable reply or query genuine business state. The originally
committed outbox intent delivers that reply within its configured retry policy.

In a production order service, derive tenant ownership from trusted ingress,
authorize command IDs, subjects, and reply destinations, and define reservation
lookup, expiry, release, and compensation policies. Store queryable reservations
by the original command ID when those operations need them. External effects use
the same canonical ID with their provider's idempotency and reconciliation protocol.
