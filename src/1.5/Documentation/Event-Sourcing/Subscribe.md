

[!include[](../../warning-banner.md)]

# Notifications

It is often convenient to have the ability to react to state changes. 
All callbacks are subject to Orleans' turn-based guarantees; see also the section on [Concurrency Guarantees](MultiInstance.md).

## Tracking Confirmed State

To be notified of any changes to the confirmed state, `JournaledGrain` subclasses can override this method:

```csharp
protected override void OnStateChanged()
{
   // read state and/or event log and take appropriate action
}
```

`OnStateChanged` is called whenever the confirmed state is updated, i.e. the version number increases. This can happen when

1. A newer version of the state was loaded from storage. 
2. An event that was raised by this instance has been successfully written to storage.
3. A notification message was received from some other instance.

Note that since all grains initially have version zero, until the initial load from storage completes, this means that `OnStateChanged` is called whenever the initial load completes with a version larger than zero.

## Tracking Tentative State

To be notified of any changes to the tentative state, `JournaledGrain` subclasses can override this method:

```csharp
protected override void OnTentativeStateChanged()
{
   // read state and/or events and take appropriate action
}
```

`OnTentativeStateChanged` is called whenever the tentative state changes, i.e. if the combined sequence  (ConfirmedEvents + UnconfirmedEvents) changes. In particular, a callback to `OnTentativeStateChanged()` always happens during `RaiseEvent`.

