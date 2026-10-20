# Messaging: stock reservations

A self-contained .NET 10 console host runs one localhost Orleans silo and two
application grains. An **order** submits a typed reservation under an application
command ID; a **stock** grain sends a deterministic typed acceptance or rejection.
The host exercises accepted, insufficient-stock, and invalid-quantity commands, each
processed once.
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

## How it works

1. The host initializes ten units of `trail-shoes`, then submits three commands through
   the order's outbox under independent IDs: `orders/order-1042/reserve-stock` for two
   units, `reserve-too-much` for nine, and `reserve-invalid-quantity` for zero.
2. `StockMessages` binds `ReserveStock`, `Restock`, and the abstract
   `ReservationOutcome` to versioned subjects; both grains inject it. Typed outbox
   `Send` and `SendReply` serialize the records, and typed `inbox.Register` handlers
   receive them decoded.
3. `OrderGrain.ReserveAsync` stages the command and awaits `WriteStateAsync`; the
   outbox delivers it once that write is durable and retries within its original
   absolute delivery budget.
4. The stock handler computes the outcome locally, then sends the reply with
   `SendReply`, updates inventory, and calls `context.Complete()` in one synchronous
   block, so the reply, the inventory, and the inbox completion commit in one journal
   write. Invalid quantities and shortages are business rejections: they reply and
   leave stock unchanged. A nonpositive `Restock` has no reply protocol, so its handler
   calls `context.Fail(reason)`.
5. The reply's ID is the command ID's `result` child. The order's handler records
   the outcome in its journaled receipts and completes, and `WaitForOutcomeAsync`
   returns once that receipt is durable. Deliveries interleave with the waiting
   request, so the reply reaches the order while the host waits for it.
6. The stock grain's inbox retains each completed command's ID and immutable
   admission cutoff. A redelivery, such as a retry whose answer was lost, receives
   `Duplicate` while that record is retained. Compaction durably fences the cutoff;
   a delayed repeat then receives `Expired` before the handler runs.

A successful run prints:

```text
ACKNOWLEDGED: orders/order-1042/reserve-stock/result, outcome=ReservationAccepted
ACKNOWLEDGED: orders/order-1042/reserve-too-much/result, outcome=ReservationRejected (InsufficientStock)
ACKNOWLEDGED: orders/order-1042/reserve-invalid-quantity/result, outcome=ReservationRejected (InvalidQuantity)
VERIFIED: remaining stock=8, reservations=1, processed requests=3.
```

The host checks each correlated outcome and that the stock grain processed each
command once and decremented stock once. A failed check or the two-minute deadline
fails the process with a nonzero exit code. Only one localhost sample using the
default silo and gateway ports should run at a time.
## Storage and production boundaries

`UseInMemoryDurableJobs` and `AddVolatileJournalStorage` keep this demonstration
standalone. Their acknowledgements exercise the real journaling, messaging pump,
and durable job. **Stopping the process discards its state.** Configure persistent
journal and job providers to recover across process restarts. The sample journals
its Orleans-serializable records with the `orleans-binary` journal format.

The receiver's journaled admission floor only advances, including across clock
changes and recovery. Its cutoff fence preserves once-only effects after
completion records are compacted. A completed duplicate acknowledges the retained
completion fact; callers obtain business results
from the original durable reply or query genuine business state. The originally
committed outbox intent delivers that reply within its configured retry policy.
An `Expired` result or sender retry exhaustion leaves earlier acceptance unknown.
Reconcile business state by the original command ID before authorizing a new send.
Applications which require crash-recoverable terminal action use an
`IOutboxMessageSource` and stage their outcome and a self-addressed follow-up
message in `OnRetired`, atomically with removing the outgoing entry.

In a production order service, derive tenant ownership from trusted ingress,
authorize command IDs, subjects, and reply destinations, and define reservation
lookup, expiry, release, and compensation policies. Store queryable reservations
by the original command ID when those operations need them. External effects use
the same canonical ID with their provider's idempotency and reconciliation protocol.
