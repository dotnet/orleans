
namespace DurableOrderProcessing;

// The body of a payment authorization request to the gateway.
public sealed record PaymentAuthorizationRequest(Guid OrderId, decimal Amount, string CardLast4);

// A gateway authorization's status monitor, in the shape of an Azure long-running operation.
public sealed record PaymentAuthorizationStatus(string Id, string Status, PaymentResult? Result);

// An authorization in the gateway's ledger, which GET /gateway/authorizations lists.
public sealed record PaymentAuthorizationEntry(string Id, Guid OrderId, decimal Amount, string Status, bool Voided, int Requests);

// A simulated external payment gateway, which the order workflow calls over HTTP through an IDurableHttpClient.
//
// The gateway honors Idempotency-Key as Stripe does. It records each key with its request's body, and answers a
// request with a known key and an equal body with the first response, marked Idempotent-Replayed. A known key with
// another body is rejected with 422 Unprocessable Content, and a request without a key with 400 Bad Request. Each key
// therefore authorizes at most one payment, whichever number of times a client retries the request.
//
// An authorization completes asynchronously: the request returns 202 Accepted with an Operation-Location status monitor,
// which reports Running until the authorization decides, and POST {monitor}:cancel voids it.
// Card numbers ending in 0002 are declined. For card numbers ending in 0003, the gateway doesn't decide, so the
// authorization runs until the order's payment timeout voids it. For card numbers ending in 0004, the gateway drops the
// connection instead of sending its first response for a key, as a network failure would after the gateway recorded the
// authorization; the client's resilience handler retries the request with the same key. For card numbers ending in
// 0005, the gateway is unavailable and answers 503 Service Unavailable, so the order's payment fails.
public sealed class PaymentGateway(TimeProvider timeProvider)
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private static readonly TimeSpan DecisionLatency = TimeSpan.FromMilliseconds(500);

    private readonly object _lock = new();
    private readonly Dictionary<string, Authorization> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Authorization> _byId = new(StringComparer.Ordinal);

    public PaymentAuthorizationEntry[] List()
    {
        lock (_lock)
        {
            return [.. _byId.Values.OrderBy(authorization => authorization.CreatedAt).Select(authorization => authorization.ToEntry(timeProvider))];
        }
    }

    public void Map(IEndpointRouteBuilder endpoints)
    {
        var gateway = endpoints.MapGroup("/gateway");
        gateway.MapGet("/authorizations", () => List());

        gateway.MapPost("/authorizations", (HttpContext context, PaymentAuthorizationRequest request) =>
        {
            if (context.Request.Headers[IdempotencyKeyHeader] is not [{ Length: > 0 } key])
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "An Idempotency-Key header is required.");
            }

            if (request.CardLast4 == "0005")
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The gateway is unavailable.");
            }

            Authorization authorization;
            int attempt;
            lock (_lock)
            {
                if (!_byKey.TryGetValue(key, out authorization!))
                {
                    authorization = new Authorization(Guid.NewGuid().ToString("N"), request, timeProvider.GetUtcNow());
                    _byKey[key] = authorization;
                    _byId[authorization.Id] = authorization;
                }
                else if (authorization.Request != request)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "The Idempotency-Key is already used.",
                        detail: "Another request used this key. A key is reused only to retry the same request.");
                }

                attempt = ++authorization.Requests;
            }

            if (attempt > 1)
            {
                context.Response.Headers["Idempotent-Replayed"] = "true";
            }

            if (attempt == 1 && request.CardLast4 == "0004")
            {
                context.Abort();
                return Results.Empty;
            }

            context.Response.Headers["Operation-Location"] = $"/gateway/authorizations/{authorization.Id}";
            return Status(context, authorization, StatusCodes.Status202Accepted);
        });

        gateway.MapGet("/authorizations/{id}", (HttpContext context, string id) =>
            Find(id) is { } authorization ? Status(context, authorization, StatusCodes.Status200OK) : Results.NotFound());

        gateway.MapPost("/authorizations/{id}:cancel", (HttpContext context, string id) =>
        {
            if (Find(id) is not { } authorization)
            {
                return Results.NotFound();
            }

            lock (_lock)
            {
                authorization.VoidedAt ??= timeProvider.GetUtcNow();
            }

            return Status(context, authorization, StatusCodes.Status200OK);
        });
    }

    private Authorization? Find(string id)
    {
        lock (_lock)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    private IResult Status(HttpContext context, Authorization authorization, int statusCode)
    {
        PaymentAuthorizationStatus status;
        lock (_lock)
        {
            status = authorization.GetStatus(timeProvider.GetUtcNow());
        }

        if (status.Status == "Running")
        {
            context.Response.Headers.RetryAfter = "1";
        }

        return Results.Json(status, statusCode: statusCode);
    }

    // An authorization in the ledger. The gateway's lock guards its mutable members.
    private sealed class Authorization(string id, PaymentAuthorizationRequest request, DateTimeOffset createdAt)
    {
        public string Id => id;

        public PaymentAuthorizationRequest Request => request;

        public DateTimeOffset CreatedAt => createdAt;

        public int Requests { get; set; }

        public DateTimeOffset? VoidedAt { get; set; }

        // The gateway decides after a latency, except for card numbers ending in 0003.
        private DateTimeOffset? DecidesAt => request.CardLast4 == "0003" ? null : createdAt + DecisionLatency;

        // A void before the decision cancels the authorization; a void after it releases the authorized payment.
        public PaymentAuthorizationStatus GetStatus(DateTimeOffset now)
        {
            if (DecidesAt is not { } decidesAt || decidesAt > (VoidedAt ?? now))
            {
                return new(id, VoidedAt is null ? "Running" : "Canceled", Result: null);
            }

            return new(id, "Succeeded", request.CardLast4 == "0002"
                ? new PaymentResult(false, "The card was declined.")
                : new PaymentResult(true, Reason: null));
        }

        public PaymentAuthorizationEntry ToEntry(TimeProvider timeProvider)
        {
            var status = GetStatus(timeProvider.GetUtcNow());
            var state = status.Result is { Succeeded: false } ? "Declined" : status.Status;
            return new(id, request.OrderId, request.Amount, state, Voided: VoidedAt is not null && status.Status == "Succeeded", Requests);
        }
    }
}
