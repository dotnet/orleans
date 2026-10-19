# Messaging: stock reservations

A self-contained .NET 10 console host runs one localhost Orleans silo and two
application grains. An **order** submits a typed reservation under an application
command ID; a **stock** grain sends a deterministic typed acceptance or rejection.
The host exercises accepted, insufficient-stock, and invalid-quantity commands.
Resubmitting each ID produces `Duplicate` admission, including rejected commands,
with one handler execution per command and only one stock decrement.
Volatile journals and in-memory jobs keep the demonstration self-contained.

## Run

**Build with locally packed Orleans sources until these APIs are published.**
The declared versions follow the repository samples' approved package baseline.
The repository build substitutes the current source packages from its local feed.

From the repository root:

```powershell
pwsh .\samples\Build-Samples.ps1 -SkipExternalAssets
# Or run Validate-Samples.ps1 to perform policy checks and the complete build.
dotnet run --project .\samples\Messaging\Messaging.csproj --configuration Release --no-build
```

`Build-Samples.ps1` packs current Orleans sources with a unique version and passes
`OrleansSamplePackageVersion` and `OrleansSamplePackageSource` to the samples
solution. The sample's NuGet references exercise the same package boundary as
application projects.

### Copying the sample out

`Messaging/` is the entire copy-out unit, including its own
`Directory.Packages.props`. Once these APIs are published, align its Orleans
package versions to that release and run:

```powershell
dotnet run --project .\Messaging.csproj --configuration Release
```

Until then, standalone builds have an explicit unpublished-package exception.
To test a copy outside this repository today, first pack Orleans using
`Build-Samples.ps1`, then copy the sample folder and replace **only the copy's**
central Orleans package versions with that local feed's generated version.
Restore/build with the local feed and the normal public dependency feed:

```powershell
dotnet restore .\Messaging.csproj --source <absolute-local-package-feed> --source https://api.nuget.org/v3/index.json
dotnet run --project .\Messaging.csproj --configuration Release --no-restore
```

For a feed with separate stable and experimental versions, use the stable version
for Server/Sdk and the experimental version for DurableJobs/Messaging/
Journaling. All transitive Orleans dependencies must come from the same source
build. The repository's `samples/Directory.Build.targets` overrides are not part
of the copy-out unit, so passing `OrleansSamplePackageVersion` alone to the copy
will not replace its declared versions.

## What to review

1. The host initializes ten units of `trail-shoes`, then submits
   `orders/order-1042/reserve-stock` for two units through the order's outbox.
   It also submits `orders/order-1042/reserve-too-much` for nine units and
   `orders/order-1042/reserve-invalid-quantity` for zero units, under independent IDs.
   For each command, once its original reply is journal-acknowledged, the host
   reconstructs the same immutable command and calls explicit inbox admission.
   Each second submission returns `Duplicate`.
2. The singleton `StockMessages` class binds `ReserveStock`, `Restock`, and abstract
   `ReservationOutcome` to `inventory.reserve.v1`, `inventory.restock.v1`, and
   `inventory.reservation-result.v1`; both grains inject it.
   Serialized sealed `ReservationAccepted` and `ReservationRejected` records
   carry the command ID, original quantity, and remaining stock.
   Rejections distinguish `InvalidQuantity` from `InsufficientStock` using
   `ReservationRejectionReason`; callers dispatch on the concrete outcome type.
   Typed helpers serialize each record into the payload of an envelope with an
   explicit subject. The outgoing message pairs that envelope with its destination;
   the receiving grain supplies the inbox message's destination.
3. Each grain registers typed handlers with chained `inbox.Register` calls in
   `OnActivateAsync`. Stock registers separate reservation and restocking methods;
   the order registers its outcome method. Registrations use method groups such as
   `Register(messages.Reserve, HandleReserveStock)`, receiving the decoded record and inbox
   context, and each message goes to the handler registered for its subject.
   The order also registers a receipts observer state during construction.
   Journal recovery restores durable state
   before `OnActivateAsync`, and recovered messages are processed by the registered
   handlers.
   The console run exercises reservation and outcome subjects; `Restock` supplies
   the additional positive-stock-increment protocol for the same stock inbox.
   Because it has no reply protocol, a nonpositive restock is permanently unusable:
   its handler calls `context.Fail("Restock quantity must be positive.")`
   and returns before mutation. Checked stock overflow throws and follows the
   bounded processing retry policy.
   Typed outbox `Send` and `SendReply` handle serialization, so the handlers work
   with ordinary records. The explicit duplicate-admission call uses
   `MessageType<T>.Create` to construct an outgoing message with the same
   command ID and encoded content, then delivers its incoming view.
4. The stock handler runs once per command. Its inbox completion record recognizes
   the same command ID during retention. The result reply uses
   the deterministic child `orders/order-1042/reserve-stock/result`; inventory
   stores the remaining stock, accepted reservation count, and execution count.
   One ID binds an immutable command, including quantity, destination, and subject.
5. The typed registration decodes each body and checks cancellation before invoking
   its method. Each handler validates and computes all results locally. Invalid
   reservation quantities and shortages are completed business rejections: they
   send a typed reply and leave stock unchanged.
   The order deliberately permits nonpositive quantities so the receiver's
   durable rejection protocol is exercised. Stock calls `SendReply`, updates
   inventory, and calls `context.Complete()` in one synchronous block.
   Inventory, the outgoing reply, and inbox completion persist in the same
   journal write, which the inbox performs after the handler returns.
6. The order's ordinary submit method awaits `WriteStateAsync` to commit its
   outgoing intent. To observe each completed round trip, the host awaits a
   command-keyed `TaskCompletionSource` observer armed before submission and
   released by the order's **receipts observer**, an `IStateMachine` registered with
   the order's journal. It captures receipts with each write and reports them from
   `OnWriteCompleted` after storage acknowledgement. These observers are host-local demo instrumentation;
   the durable reply is the outcome in the order grain's journaled receipts.

A successful run prints:

```text
ACKNOWLEDGED: orders/order-1042/reserve-stock/result, outcome=ReservationAccepted
RESUBMITTED: orders/order-1042/reserve-stock, admission=Duplicate
ACKNOWLEDGED: orders/order-1042/reserve-too-much/result, outcome=ReservationRejected (InsufficientStock)
RESUBMITTED: orders/order-1042/reserve-too-much, admission=Duplicate
ACKNOWLEDGED: orders/order-1042/reserve-invalid-quantity/result, outcome=ReservationRejected (InvalidQuantity)
RESUBMITTED: orders/order-1042/reserve-invalid-quantity, admission=Duplicate
VERIFIED: remaining stock=8, reservations=1, processed requests=3.
```

The assertions verify all three correlated concrete outcome types/reasons, duplicate admission
for accepted and rejected commands, exactly one handler execution per ID, unchanged
stock for both rejections, and exactly one stock decrement. Every outcome wait uses
an explicit journal-acknowledgement signal, without sleeps or polling.
A failed assertion or the two-minute deadline
fails the process with a nonzero exit code. The host stops after the verification.
Only one localhost sample using the default silo/gateway ports should run at a time.

## Storage and production boundaries

`UseInMemoryDurableJobs` and `AddVolatileJournalStorage` keep this demonstration
standalone. Their acknowledgements exercise the real journaling, messaging pump,
and durable job. **Stopping the process discards its state.** Configure persistent
journal and job providers to recover across process restarts. The sample journals
its Orleans-serializable records with the `orleans-binary` journal format.

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
