// =============================================================================
// ORDER SAGA SAMPLE: Demonstrating DurableTask Workflows
// =============================================================================
//
// This sample shows how to use DurableTask for orchestrating multi-step
// workflows that survive grain restarts and crashes.
//
// KEY CONCEPTS:
// - DurableTask<T> is a special return type that enables workflow orchestration
// - .WithId("step-name") makes sub-steps idempotent (safe to replay)
// - .ScheduleAsync() starts asynchronous execution with tracking
// - Workflows can call other grains and compose child workflows
// - Perfect for: sagas, long-running processes, multi-step operations
//
// =============================================================================

using System.Distributed.DurableTasks;
using Orleans.Journaling;

namespace WorkflowsApp.Service.Samples.OrderSaga;

/// <summary>
/// A durable order processing saga that demonstrates workflow orchestration.
/// </summary>
internal static class OrderSaga
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();

        Console.WriteLine("--- OrderSaga Sample: Demonstrating DurableTask Workflows ---");
        Console.WriteLine();

        // Create an order processor grain
        var processor = grainFactory.GetGrain<IOrderProcessorSagaGrain>("order-001");

        // Create an order
        var order = new OrderDetails
        {
            CustomerId = "customer-123",
            Items = ["Widget A", "Widget B", "Gadget C"],
            TotalAmount = 99.99m
        };

        Console.WriteLine($"Processing order for customer: {order.CustomerId}");
        Console.WriteLine($"Items: {string.Join(", ", order.Items)}");
        Console.WriteLine($"Total: ${order.TotalAmount}");
        Console.WriteLine();

        // Start the order processing workflow
        // ScheduleAsync() starts the workflow and returns immediately with a handle
        var workflowHandle = await processor.ProcessOrder(order).ScheduleAsync("process-order-001");

        Console.WriteLine($"Workflow started with ID: {workflowHandle.Id}");
        Console.WriteLine("Waiting for workflow to complete...");
        Console.WriteLine();

        // Wait for the workflow to complete and get the result
        var result = await workflowHandle.WaitAsync();

        Console.WriteLine($"Workflow completed!");
        Console.WriteLine($"  Order Status: {result.Status}");
        Console.WriteLine($"  Tracking Number: {result.TrackingNumber ?? "N/A"}");
        Console.WriteLine($"  Message: {result.Message}");

        Console.WriteLine("\nOrderSaga sample completed!");
        Console.WriteLine("Note: Workflow state persists across restarts.\n");
    }

    // -------------------------------------------------------------------------
    // DATA MODELS
    // -------------------------------------------------------------------------

    [GenerateSerializer]
    public class OrderDetails
    {
        [Id(0)] public required string CustomerId { get; init; }
        [Id(1)] public required List<string> Items { get; init; }
        [Id(2)] public decimal TotalAmount { get; init; }
    }

    [GenerateSerializer]
    public class OrderResult(OrderStatus status, string? trackingNumber, string? message)
    {
        [Id(0)] public OrderStatus Status { get; init; } = status;
        [Id(1)] public string? TrackingNumber { get; init; } = trackingNumber;
        [Id(2)] public string? Message { get; init; } = message;
    }

    [GenerateSerializer]
    public enum OrderStatus
    {
        Pending,
        PaymentProcessed,
        InventoryReserved,
        Shipped,
        Failed
    }

    // -------------------------------------------------------------------------
    // GRAIN INTERFACES
    // -------------------------------------------------------------------------

    [Alias("IOrderProcessorSagaGrain")]
    public interface IOrderProcessorSagaGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Processes an order through multiple steps: payment, inventory, shipping.
        /// This is a durable workflow that survives failures.
        /// </summary>
        [Alias("ProcessOrder")]
        DurableTask<OrderResult> ProcessOrder(OrderDetails order);
    }

    [Alias("IPaymentSagaGrain")]
    public interface IPaymentSagaGrain : IGrainWithStringKey
    {
        /// <summary>Charges a customer and returns a payment confirmation.</summary>
        [Alias("ChargeCustomer")]
        DurableTask<string> ChargeCustomer(string customerId, decimal amount);
    }

    [Alias("ISagaInventoryGrain")]
    public interface ISagaInventoryGrain : IGrainWithStringKey
    {
        /// <summary>Reserves inventory for items. Returns true if successful.</summary>
        [Alias("ReserveItems")]
        DurableTask<bool> ReserveItems(List<string> items);
    }

    [Alias("IShippingSagaGrain")]
    public interface IShippingSagaGrain : IGrainWithStringKey
    {
        /// <summary>Creates a shipment and returns a tracking number.</summary>
        [Alias("CreateShipment")]
        DurableTask<string> CreateShipment(string customerId, List<string> items);
    }

    // -------------------------------------------------------------------------
    // GRAIN IMPLEMENTATIONS
    // -------------------------------------------------------------------------

    /// <summary>
    /// The orchestrator grain that coordinates the order processing workflow.
    ///
    /// HOW DURABLE WORKFLOWS WORK:
    ///
    /// 1. The method returns DurableTask<T> instead of Task<T>
    /// 2. Each step is tagged with .WithId("unique-id")
    /// 3. When the workflow restarts after a failure:
    ///    - Steps that already completed are NOT re-executed
    ///    - Their previous results are replayed from the journal
    ///    - Execution resumes from where it left off
    ///
    /// KEY PATTERN: .WithId("step-name")
    /// - Makes each step idempotent
    /// - Prevents duplicate work on replay
    /// - The ID must be unique within the workflow
    /// </summary>
    internal class OrderProcessorSagaGrain : DurableGrain, IOrderProcessorSagaGrain
    {
        public async DurableTask<OrderResult> ProcessOrder(OrderDetails order)
        {
            try
            {
                // Step 1: Process payment
                // .WithId ensures this step is only executed once
                var payment = GrainFactory.GetGrain<IPaymentSagaGrain>("payments");
                var paymentConfirmation = await payment.ChargeCustomer(order.CustomerId, order.TotalAmount).WithId("charge-payment");

                Console.WriteLine($"  [Step 1] Payment processed: {paymentConfirmation}");

                // Step 2: Reserve inventory
                var inventory = GrainFactory.GetGrain<ISagaInventoryGrain>("warehouse");
                var reserved = await inventory.ReserveItems(order.Items).WithId("reserve-inventory");

                if (!reserved)
                {
                    return new OrderResult(OrderStatus.Failed, null, "Insufficient inventory");
                }

                Console.WriteLine($"  [Step 2] Inventory reserved: {reserved}");

                // Step 3: Create shipment
                var shipping = GrainFactory.GetGrain<IShippingSagaGrain>("shipping");
                var trackingNumber = await shipping.CreateShipment(order.CustomerId, order.Items).WithId("create-shipment");

                Console.WriteLine($"  [Step 3] Shipment created: {trackingNumber}");

                return new OrderResult(OrderStatus.Shipped, trackingNumber, "Order shipped successfully!");
            }
            catch (Exception ex)
            {
                return new OrderResult(OrderStatus.Failed, null, $"Order failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Simulates a payment service.
    /// </summary>
    internal class PaymentSagaGrain : DurableGrain, IPaymentSagaGrain
    {
        public DurableTask<string> ChargeCustomer(string customerId, decimal amount)
        {
            // Simulate payment processing
            var confirmationId = $"PAY-{Guid.NewGuid().ToString("N")[..8].ToUpper()}";
            return DurableTask.FromResult(confirmationId);
        }
    }

    /// <summary>
    /// Simulates an inventory service.
    /// </summary>
    internal class InventorySagaGrain : DurableGrain, ISagaInventoryGrain
    {
        public DurableTask<bool> ReserveItems(List<string> items)
        {
            // Simulate inventory check - always succeeds for demo
            return DurableTask.FromResult(true);
        }
    }

    /// <summary>
    /// Simulates a shipping service.
    /// </summary>
    internal class ShippingSagaGrain : DurableGrain, IShippingSagaGrain
    {
        public DurableTask<string> CreateShipment(string customerId, List<string> items)
        {
            // Simulate shipment creation
            var trackingNumber = $"SHIP-{Guid.NewGuid().ToString("N")[..12].ToUpper()}";
            return DurableTask.FromResult(trackingNumber);
        }
    }
}
