# Durable Messaging: stock reservations

A self-contained .NET 10 console host runs one localhost Orleans silo and two
application grains: an **order** sends a stock reservation and a **stock** grain
records the outcome and replies. Volatile journals and in-memory jobs keep the
demonstration self-contained.

## Run

**Build with locally packed Orleans sources until these APIs are published.**
The declared versions follow the repository samples' approved package baseline.
The repository build substitutes the current source packages from its local feed.

From the repository root:

```powershell
pwsh ./samples/Build-Samples.ps1 -SkipExternalAssets
# Or run Validate-Samples.ps1 to perform policy checks and the complete build.
dotnet run --project ./samples/DurableMessaging/DurableMessaging.csproj --configuration Release --no-build
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
dotnet run --project ./DurableMessaging.csproj --configuration Release
```

Until then, standalone builds have an explicit unpublished-package exception.
To test a copy outside this repository today, first pack Orleans using
`Build-Samples.ps1`, then copy the sample folder and replace **only the copy's**
central Orleans package versions with that local feed's generated version.
Restore/build with the local feed and the normal public dependency feed:

```powershell
dotnet restore ./DurableMessaging.csproj --source <absolute-local-package-feed> --source https://api.nuget.org/v3/index.json
dotnet run --project ./DurableMessaging.csproj --configuration Release --no-restore
```

For a feed with separate stable and experimental versions, use the stable version
for Server/Sdk and the experimental version for DurableJobs/DurableMessaging/
Journaling. All transitive Orleans dependencies must come from the same source
build. The repository's `samples/Directory.Build.targets` overrides are not part
of the copy-out unit, so passing `OrleansSamplePackageVersion` alone to the copy
will not replace its declared versions.

## What to review

1. The host initializes ten units of `trail-shoes`, then submits the same
   `orders/order-1042/reserve-stock/v1` business operation twice for two units.
   Each submission creates a **fresh transport message ID**. Transport retries
   preserve their original ID.
2. `StockMessage`, `ReserveStock`, and `ReservationOutcome` define the application's
   encoding, message dispatch, and reply destination. The transport envelope
   carries only sender, receiver, message ID, and opaque `ArcBuffer` payload bytes.
3. Each grain registers a **non-generic `IInboxHandler`** and decodes using the
   ordinary `Serializer<StockMessage>.Deserialize(ArcBuffer)` overload. Each
   activation owns a reusable `ArcBufferWriter`. Locally created envelopes are
   owned `using` variables; handlers borrow inbox envelopes through method
   completion. `IDurableOutbox.Send` synchronously retains its own slice.
4. The stock grain's journaled ledger uses a stable **`HierarchicalKey` business
   operation**. A duplicate returns the original reservation
   ID and preserves the current stock count. Reusing that key
   with a different quantity is invalid. Rejected reservations are also recorded
   as stable outcomes.
5. Each handler decodes, validates, computes results, constructs any outgoing
   envelope, and checks cancellation **before its first shared mutation**. From
   that mutation through `context.Complete()` and method return, execution is synchronous.
   Business state, ledger, outgoing intent, and inbox completion share the journal
   boundary. The runtime owns the subsequent write and acknowledgement.
6. The order's ordinary submit method awaits `WriteStateAsync` to commit its
   outgoing intent. To observe the completed round trip, the host awaits a
   `TaskCompletionSource` observer released
   by the order's journal **after-acknowledgement hook**. The hook reports a snapshot
   taken before capture. The observer is host-local demo instrumentation;
   the durable reply is the outcome in the order grain's journaled receipts.

A successful run prints distinct submission IDs and then:

```text
ACKNOWLEDGED: two replies, original reservation <reservation-id>
VERIFIED: remaining stock=8, reservations=1, processed requests=2, ledger entries=1.
```

The assertions verify equal original outcomes, exactly two processed requests,
and exactly one business effect. A failed assertion or the two-minute deadline
fails the process with a nonzero exit code. The host stops after the verification.
Only one localhost sample using the default silo/gateway ports should run at a time.

## Storage and production boundaries

`UseInMemoryDurableJobs` and `AddVolatileJournalStorage` keep this demonstration
standalone. Their acknowledgements exercise the real journaling, inbox, outbox,
and job pumps. **Stopping the process discards its state.** Configure persistent
journal and job providers to recover across process restarts.

Retain business-operation outcomes for the application's deduplication window;
business idempotency is enforced by the saved outcome. In a production order
service, authenticate and authorize submitters and reply destinations,
validate the complete request fingerprint, and define retention and compensation
policies. Use the external provider's idempotency and reconciliation protocol for
external effects.
