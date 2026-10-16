# Microsoft Orleans Durable Tasks HTTP

> [!WARNING]
> This source assembly contains an incubating Orleans integration and is not
> published as a NuGet package. Its APIs and wire protocol can change while the
> integration is evaluated.

`Microsoft.Orleans.DurableTasks.Http` carries the idempotence of Orleans durable tasks
across HTTP. Route groups of ASP.NET Core Minimal APIs start durable grain calls as
long-running HTTP operations, keyed by the request's `Idempotency-Key`, and serve
their status, results, event streams, cancellation, and signals.

## Serving durable operations

```csharp
builder.Services.AddDurableHttp();

var app = builder.Build();

var orders = app.MapGroup("/orders/{orderId}")
    .WithDurableOperations()
    .RequireAuthorization("orders");

orders.MapPost("/place", (string orderId, PlaceOrder order, IGrainFactory grains) =>
    grains.GetGrain<IOrderGrain>(orderId).PlaceAsync(order));
orders.MapDurableSignal(OrderGrain.Approval);
```

`WithDurableOperations` makes each endpoint of the group whose handler returns a
durable grain call start an operation which performs it. The handler binds its
parameters like any Minimal API handler and runs for each request, including retries;
it creates the call, and the endpoint starts it. Other endpoints of the group are
unchanged. Authentication and authorization are the application's: policies and
endpoint filters on the group cover its starts and the routes which serve its
operations.

The request's `Idempotency-Key` names the operation, so each retry of the request
reaches the operation which its first attempt started. An operation identifier has
1 to 255 printable ASCII characters other than `/`. The operation's key is the path
of its group, with the request's route values, and its identifier, such as
`orders/42/{id}`, so each order has its own operations. A scope, such as
`WithDurableOperations("orders")`, replaces the path: groups of one scope and equal route
values address the same operations.
`DurableHttpOptions.MapOperationId` maps identifiers on every request, so the
application scopes them by what is relevant to its business, such as an account, as
Stripe scopes keys to accounts.

An operation host grain, which the operation's key addresses, starts the call as a
root task on its grain, named by the operation's key, and completes with its outcome,
so requests which observe, cancel, or signal the operation reach it by its identifier
alone. A handler which returns a
reference to an existing task, such as `grain.GetDurableTask<Receipt>(id)`, starts an
operation which subscribes to that task: the operation reports the task's outcome,
and its cancellation and signals reach the task:

```csharp
orders.MapPost("/fulfillment", (string orderId, IGrainFactory grains) =>
    grains.GetGrain<IOrderGrain>(orderId).GetDurableTask<Shipment>("fulfillment"));
```

A start responds:

| Response | When |
| --- | --- |
| `200 OK` with the result, or `204 No Content` | The operation succeeded, including within the wait which `Prefer: wait=N` requests. |
| `202 Accepted` with the status monitor | The operation is running, including for a retry which arrives before it completes, as Azure repeatable requests and Stripe's v2 idempotency resume work. |
| `424 Failed Dependency` with the status monitor | The operation failed or was canceled, including when its grain rejected the call. |
| `400 Bad Request` | The `Idempotency-Key` is invalid, or missing when `RequireIdempotencyKey` is set. |
| `422 Unprocessable Content` | Another request used the key to start other work. |
| `502 Bad Gateway` | The operation's host didn't accept it, so the request can be sent again. |

Responses which identify an operation carry an `Operation-Location` header, the URL
of its status monitor:

```http
HTTP/1.1 202 Accepted
Operation-Location: https://example.com/orders/42/operations/8e03978e-40d5-43e8-bc93-6894a57f9324
Retry-After: 1

{"id":"8e03978e-40d5-43e8-bc93-6894a57f9324","status":"Running"}
```

The status monitor has the shape of an Azure long-running operation: `status` is
`Running`, `Succeeded`, `Failed`, or `Canceled`; a succeeded operation carries its
`result`, and a failed or canceled operation carries an `error` with a `code` and a
`message`. `DurableHttpOptions.CreateError` describes exceptions to clients; its
default reports only `Failed` or `Canceled`, so exception details stay on the server
unless the application maps the exceptions it defines for its clients. The group
serves its operations:

- `GET {group}/operations/{id}` returns the status monitor. With `Prefer: wait=N`,
  the response waits up to `N` seconds, at most `DurableHttpOptions.MaxWait`, for the
  operation to complete. A running operation's response carries `Retry-After`.
- `GET {group}/operations/{id}/events` streams the status monitor as server-sent
  `status` events, identified by their status: the current status, then the terminal
  status of a running operation, after which the stream ends. A reconnect with a
  terminal `Last-Event-ID` receives `204 No Content`, which ends an `EventSource`'s
  reconnects.
- `POST {group}/operations/{id}:cancel` durably requests cancellation, with an
  optional `{"reason": "..."}` body which the task reads from
  `DurableTask.Current.TaskCancellationReason`, and returns `202 Accepted`. The task's
  body decides its outcome, which the status monitor reports.
- `POST {group}/operations/{id}/signals/{name}` delivers a signal which
  `MapDurableSignal` maps, with the JSON body as its value, and returns
  `204 No Content` once the task durably holds it, including for a repeated signal
  with an equal value. A different value, or a signal to an operation which
  completed without it, returns `409 Conflict`.

The host retains a completed operation, and so deduplicates its key and serves its
status monitor, for `DurableTaskOptions.ResultRetentionPeriod`.

The endpoints which serve operations bind requests without reflection, so they serve
trimmed and NativeAOT applications. Signal values and operation results serialize with
the Minimal API `JsonOptions`, whose resolver, such as a source-generated
`JsonSerializerContext`, handles those types in such applications.
