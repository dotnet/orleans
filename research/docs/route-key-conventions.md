# Orleans Durable Messaging Route Key Conventions

## Overview

Route keys are string identifiers used to route durable messages to appropriate handlers in the Orleans.Journaling messaging system. This document describes the standardized route key conventions used across Orleans components.

## Route Key Structure

Route keys use forward slashes (`/`) as hierarchical separators, enabling prefix-based routing patterns. For example:
- `rpc/request` - specific route for RPC requests
- `rpc/reply` - specific route for RPC replies
- `rpc/` - prefix matching all RPC-related routes

### Escaping Rules

Special characters in route key segments must be escaped:
- Forward slash (`/`) is the segment separator
- Backslash (`\`) is the escape character
- To include a literal `/` in a segment, use `\/`
- To include a literal `\` in a segment, use `\\`

## Standard Route Prefixes

### `rpc/` - Remote Procedure Call Messages

Used for durable RPC-style request/response patterns between grains.

| Route Key | Purpose | Direction |
|-----------|---------|-----------|
| `rpc/request` | Durable RPC request | Caller → Responder |
| `rpc/reply` | Durable RPC response (success or error) | Responder → Caller |
| `rpc/notify` | One-way notification (no reply expected) | Sender → Receiver |

**Example: Request/Reply Pattern**

```csharp
// Sender creates request with ReplyTo
var request = envelope.CreateEnvelope()
    .To(responderGrainId, "rpc/request")
    .WithReplyTo(this.GetGrainId())
    .WithCorrelationKey(HierarchicalKey.Create("order-123"))
    .WithBody(new ProcessOrderRequest { OrderId = "order-123" })
    .Build();

inbox.Send(request);

// Responder processes request and sends reply
public class OrderProcessorHandler : RouteKeyHandler
{
    public OrderProcessorHandler() : base("rpc/request") { }
    
    protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
    {
        if (!context.Envelope.Data.TryGetBody<ProcessOrderRequest>(out var request))
        {
            context.SendError("DESERIALIZATION_FAILED", "Failed to deserialize request");
            return;
        }
        
        var result = await ProcessOrder(request, ct);
        
        // Send reply to ReplyTo address
        if (context.Envelope.ReplyTo is { } replyTo)
        {
            var reply = context.CreateEnvelope()
                .To(replyTo, "rpc/reply")
                .WithCorrelationKey(context.Envelope.CorrelationKey)
                .WithBody(new ProcessOrderResponse { Success = true, OrderId = result.OrderId })
                .Build();
            
            context.Send(reply);
        }
    }
}

// Caller handles reply
public class OrderCallerHandler : RouteKeyHandler
{
    public OrderCallerHandler() : base("rpc/reply") { }
    
    protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
    {
        if (context.Envelope.Data.TryGetBody<ProcessOrderResponse>(out var response))
        {
            // Handle successful response
            Console.WriteLine($"Order {response.OrderId} processed successfully");
        }
        else if (context.Envelope.Data.TryGetBody<DurableErrorResponse>(out var error))
        {
            // Handle error response
            Console.WriteLine($"Order processing failed: {error.Message}");
        }
        
        return ValueTask.CompletedTask;
    }
}
```

### `durabletask/` - DurableTask Transport Messages

Used by Orleans.DurableTask for durable workflow execution and orchestration. These routes facilitate communication between workflow instances and their activities.

| Route Key | Purpose | Description |
|-----------|---------|-------------|
| `durabletask/schedule` | Schedule task execution | Request to start a new task or activity |
| `durabletask/complete` | Task completion notification | Notifies parent workflow of task completion |
| `durabletask/event` | External event delivery | Delivers external events to running workflows |

**Note**: The exact route keys for `durabletask/` are implementation-specific and may vary. Consult Orleans.DurableTask documentation for current conventions.

### `job/` - DurableJob Scheduling Messages

Used by Orleans.DurableJobs for scheduled and recurring job execution.

| Route Key | Purpose | Description |
|-----------|---------|-------------|
| `job/trigger` | Job trigger request | Request to execute a scheduled job |
| `job/complete` | Job completion notification | Notifies scheduler of job completion |
| `job/status` | Job status query | Query current job status |

**Note**: The exact route keys for `job/` are implementation-specific and may vary. Consult Orleans.DurableJobs documentation for current conventions.

## Routing Patterns

### Exact Route Matching

Use `RouteKeyHandler` for exact route matching:

```csharp
public class PaymentHandler : RouteKeyHandler
{
    public PaymentHandler() : base("payment/process") { }
    
    protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
    {
        // Only handles messages with route key "payment/process"
        return ProcessPayment(context, ct);
    }
}
```

### Prefix-Based Routing

Use `RoutePrefixHandler` to handle all routes with a specific prefix:

```csharp
public class RpcPrefixHandler : RoutePrefixHandler
{
    public RpcPrefixHandler() : base("rpc/") { }
    
    protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
    {
        // Handles all routes starting with "rpc/"
        // Use GetRouteSuffix() to determine the operation
        var operation = GetRouteSuffix(context.Envelope.RouteKey);
        
        switch (operation)
        {
            case "request":
                await HandleRequest(context, ct);
                break;
            case "reply":
                await HandleReply(context, ct);
                break;
            case "notify":
                await HandleNotify(context, ct);
                break;
            default:
                context.SendError("UNKNOWN_OPERATION", $"Unknown RPC operation: {operation}");
                break;
        }
    }
}
```

### Correlation-Based Routing

Use `CorrelationHandler` to handle messages based on correlation key relationships:

```csharp
public class WorkflowHandler : CorrelationHandler
{
    public WorkflowHandler(string workflowId) 
        : base(HierarchicalKey.Create($"workflow/{workflowId}"))
    {
    }
    
    protected override ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
    {
        // Handles messages where CorrelationKey equals or is a child of "workflow/{workflowId}"
        // For example:
        // - "workflow/123" (exact match)
        // - "workflow/123/step-1" (child)
        // - "workflow/123/step-1/retry-2" (grandchild)
        
        return ProcessWorkflowMessage(context, ct);
    }
}
```

## Handler Registration Order

Handlers are evaluated in registration order. The first handler whose `CanHandle()` method returns `true` processes the message.

**Best Practice**: Register specific handlers before generic prefix handlers:

```csharp
// Register specific handlers first
inbox.RegisterHandler(new SpecificOrderHandler());      // Exact match: "order/process"
inbox.RegisterHandler(new SpecificPaymentHandler());    // Exact match: "payment/process"

// Then register prefix handlers
inbox.RegisterHandler(new OrderPrefixHandler());        // Prefix match: "order/"
inbox.RegisterHandler(new RpcPrefixHandler());          // Prefix match: "rpc/"

// Finally register catch-all handlers
inbox.RegisterHandler(new FallbackHandler());           // Matches everything
```

## Error Responses

Error responses use the same route as success responses (typically `rpc/reply`). The response body contains a `DurableErrorResponse` instead of the expected success response type.

### Standard Error Codes

| Error Code | Description | Retriable |
|------------|-------------|-----------|
| `HANDLER_NOT_FOUND` | No handler registered for route key | No |
| `DESERIALIZATION_FAILED` | Failed to deserialize message body | No |
| `HANDLER_EXCEPTION` | Handler threw an unhandled exception | Maybe |
| `CANCELLED` | Operation was cancelled | Maybe |
| `TIMEOUT` | Operation timed out | Yes |

### Sending Error Responses

```csharp
public class SafeHandler : RouteKeyHandler
{
    public SafeHandler() : base("order/process") { }
    
    protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
    {
        try
        {
            if (!context.Envelope.Data.TryGetBody<OrderRequest>(out var request))
            {
                // Deserialization failed
                context.SendError("DESERIALIZATION_FAILED", "Invalid order request format");
                return;
            }
            
            await ProcessOrder(request, ct);
        }
        catch (OperationCanceledException)
        {
            // Operation cancelled
            context.SendError("CANCELLED", "Order processing was cancelled", isRetriable: true);
        }
        catch (Exception ex)
        {
            // Unhandled exception
            context.SendError(ex, isRetriable: false);
        }
    }
}
```

## Custom Route Key Conventions

When defining custom route keys for your application:

1. **Use hierarchical prefixes** - Group related operations under a common prefix (e.g., `myapp/orders/`, `myapp/inventory/`)
2. **Be consistent** - Use consistent naming conventions (lowercase, hyphens for multi-word segments)
3. **Document conventions** - Document your route key conventions for your team
4. **Avoid collisions** - Don't use route keys that conflict with Orleans standard prefixes
5. **Use versioning** - Include version in route keys when needed (e.g., `api/v1/orders`, `api/v2/orders`)

### Example Custom Conventions

```csharp
// Good: Hierarchical, consistent naming
"inventory/query"
"inventory/update"
"inventory/restock"

"orders/v1/create"
"orders/v1/cancel"
"orders/v1/status"

// Avoid: Flat structure without grouping
"query-inventory"
"update-inventory"
"restock-inventory"
```

## Performance Considerations

### Route Caching

The inbox implementation caches route-to-handler mappings after the first lookup. This provides O(1) lookup performance for subsequent messages with the same route key.

The cache is invalidated when new handlers are registered (a rare operation typically done during grain initialization).

### Handler Selection Performance

- **Exact route matching** (`RouteKeyHandler`): O(1) cached lookup
- **Prefix matching** (`RoutePrefixHandler`): O(1) cached lookup after first match
- **Correlation matching** (`CorrelationHandler`): O(n) linear scan through handlers, then cached by route key

For high-throughput scenarios with many handlers, consider:
1. Register handlers with exact route keys when possible
2. Use prefix handlers to consolidate related routes
3. Register frequently-used handlers first (registration order matters)

## See Also

- [Unified Durable Messaging System RFC](./TDD-unified-durable-messaging-system.md) - Comprehensive technical design
- [Durable Messaging System Consolidation](./2026-01-17-durable-messaging-system-consolidation.md) - Research document
- Orleans.Journaling API Documentation - Handler base classes and interfaces
