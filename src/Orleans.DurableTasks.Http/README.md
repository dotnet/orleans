# Microsoft Orleans Durable Tasks HTTP

> [!WARNING]
> This source assembly contains an incubating Orleans integration and is not
> published as a NuGet package. Its APIs and wire protocol can change while the
> integration is evaluated.

`Microsoft.Orleans.DurableTasks.Http` carries the idempotence of Orleans durable tasks
across HTTP. Route groups of ASP.NET Core Minimal APIs start durable grain calls as
long-running HTTP operations, keyed by the request's `Idempotency-Key`, and serve
their status, results, event streams, cancellation, signals, and webhooks. An
`IDurableHttpClient` runs HTTP requests and operations as steps of durable tasks, and
sends each step's identity as its `Idempotency-Key`, so a task which retries or
replays a request reaches the operation which its first attempt started.

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
`WithDurableOperations("orders")`, replaces the path: groups and Nexus services of
one scope and equal route values address the same operations.
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

## Serving durable operations through Nexus

Temporal's Nexus protocol starts and cancels operations by service and operation
name. Nexus services start durable grain calls as the same operations:

```csharp
var payments = app.MapGroup("/nexus").MapNexusService("payments.v1", scope: "payments");
payments.MapNexusOperation("charge", (Charge input, IGrainFactory grains) =>
    grains.GetGrain<IPaymentGrain>(input.AccountId).ChargeAsync(input));

app.MapGroup("/nexus").WithDurableOperations("payments").MapDurableSignal(PaymentGrain.Capture);
```

- `POST /nexus/payments.v1/charge` starts the call as the operation which the
  request's `Nexus-Request-Id` names. It returns `200 OK` with the result and
  `Nexus-Operation-State: succeeded` for a succeeded operation, `201 Created` with an
  `OperationInfo` whose `token` addresses the operation while it runs, and
  `424 Failed Dependency` with an `OperationError` failure for a failed or canceled
  operation.
- `POST /nexus/payments.v1/charge/cancel` durably requests cancellation of the
  operation which its `Nexus-Operation-Token` header or `token` query value names,
  and returns `202 Accepted`.
- Invalid, conflicting, and unknown requests receive Nexus handler errors:
  `400 BAD_REQUEST`, `409 CONFLICT`, and `404 NOT_FOUND`. A start which the
  operation's host doesn't accept receives `503 UNAVAILABLE`, which the caller retries.

`MapNexusOperation` binds its handler's parameters by reflection, as Minimal APIs bind
a `MapPost` handler which the request delegate generator doesn't intercept.

A token is the operation's identifier; register an `INexusOperationTokenFormat` to
sign, encrypt, or otherwise format tokens. A group of the service's scope without
route parameters addresses the service's operations by their Nexus request
identifiers, so it streams their status and delivers their signals.

## Callbacks

A request which starts an operation registers a callback URL, which receives the
operation's outcome once it is terminal: an `Operation-Callback` header, whose
delivery carries the status monitor with Standard Webhooks headers, or a Nexus
`callback` query value, whose delivery is a Nexus completion. Each delivery carries
the request's headers which start with `Operation-Callback-` or `Nexus-Callback-`,
without the prefix, such as a receiver's credentials. Headers which address the
connection, such as `Host`, `Transfer-Encoding`, and `Proxy-*`, and headers which
deliveries set themselves, such as `webhook-*` and `Nexus-Operation-*`, receive
`400 Bad Request`.

```csharp
builder.Services.AddDurableHttp(options =>
    options.AllowCallback = (context, url) => url.Scheme == "https" && IsTrustedReceiver(context.User, url));

siloBuilder.Services.AddDurableHttpCallbacks();
siloBuilder.Services.AddSingleton<IDurableWebhookSigner, MyWebhookSigner>();
```

`DurableHttpOptions.AllowCallback` decides which URLs the application accepts; by
default it accepts none, and a request with another URL receives `400 Bad Request`.
Each callback is a durable task on a relay grain in the silos, which subscribes to
the operation and delivers its outcome, retrying with backoff and `Retry-After` until
the receiver answers `2xx`, or `410 Gone`, or `DurableCallbackOptions.RetryTimeout`
elapses. A retried start registers the same callback, which delivers once.
Deliveries are at least once: a webhook's `webhook-id` is equal for each attempt, and
an `IDurableWebhookSigner`, which holds the application's keys, adds
`webhook-signature`. `AddDurableHttpCallbacks` returns the builder of the
`HttpClient` which delivers callbacks, to configure its handlers.

## Calling HTTP APIs from durable tasks

```csharp
siloBuilder.Services.AddDurableHttpClient("payments")
    .ConfigureHttpClient(client => client.BaseAddress = paymentsUri)
    .AddStandardResilienceHandler();

public sealed class OrderGrain([FromKeyedServices("payments")] IDurableHttpClient payments) : DurableGrain, IOrderGrain
{
    public async DurableTask<Receipt> PlaceAsync(PlaceOrder order)
    {
        var payment = await payments.StartOperationAsync(() => new HttpRequestMessage(HttpMethod.Post, "authorizations")
        {
            Content = JsonContent.Create(new Authorization(order.Account, order.Total)),
        });

        try
        {
            var approver = await Approval.WaitAsync();
            await payments.SignalOperationAsync(payment.Location!, "capture", order.Total);
            return await payments.WaitForOperationAsync<Receipt>(payment, new() { Mode = DurableHttpWaitMode.Events });
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            await payments.CancelOperationAsync(payment.Location!, DurableTask.Current.TaskCancellationReason).Shielded();
            throw;
        }
    }
}
```

`AddDurableHttpClient` registers an `IDurableHttpClient`, which grains inject as a
keyed service, and returns the builder of the `HttpClient` which it sends requests
with. Each of its methods which returns a durable task records its responses as steps
of the calling task, so a replay reads them without sending the requests again. Each
step's unsafe requests carry an `Idempotency-Key` derived from the service, the grain,
and the step's identifier, so a step which runs again after a failure or a
deactivation reaches the same server-side work. The key handler precedes the
handlers which the builder adds, so their retries carry the key.

- `SendAsync` sends a request and returns the recorded `DurableHttpResponse`.
- `RunOperationAsync<T>` starts an operation and durably waits for its result;
  `StartOperationAsync` and `WaitForOperationAsync<T>` do so in two steps, with other
  work between them.
- `GetOperationAsync`, `CancelOperationAsync`, and `SignalOperationAsync` read,
  cancel, and signal an operation by its status monitor's URL.
- `WatchOperationAsync` streams an operation's statuses from its events, outside
  durable executions, until it is terminal.

A `200 OK` or `204 No Content` start response carries the result, as does another
`2xx` response without a status monitor. A `424 Failed Dependency` response reports
a failed or canceled operation. Any other response which locates the operation runs
it: an `Operation-Location` or `Azure-AsyncOperation` status monitor, such as this
package's operations and Azure long-running operations serve, including on an Azure
`201 Created` response, or a `Location` on a `202 Accepted` response, which answers
`202 Accepted` until it returns the result. `DurableHttpWaitMode.Poll` polls after the interval which
`Retry-After` advertises, or `DurableHttpOperationOptions.PollInterval`, with durable
delays between polls; `DurableHttpWaitMode.Events` reads the status monitor's events
within one step, which learns the outcome as soon as the server reports it. A failed
or canceled operation throws `DurableHttpOperationException` with its status, code,
and message.

`DurableHttpClientOptions.SerializerOptions` serializes request values and results,
with the Minimal API JSON defaults unless configured. A trimmed or NativeAOT
application sets options whose resolver handles its request and result types.

`AddDurableIdempotencyKey` adds the key handler to any `HttpClient`, for durable tasks
which send requests from their own steps with `DurableTask.Run`.
