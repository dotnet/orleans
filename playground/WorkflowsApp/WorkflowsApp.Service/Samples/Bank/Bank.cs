using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.DurableTasks;
using Orleans.Journaling;

namespace WorkflowsApp.Service.Samples.Bank;

internal static class Bank
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var client = services.GetRequiredService<IClusterClient>();

        var bank = client.GetGrain<IBankGrain>("bank");
        var customer = client.GetGrain<IAccountGrain>("customer");
        var business = client.GetGrain<IAccountGrain>("business");

        // await await?! awaiting ScheduleAsync yields a ScheduledTask, promising that the task has been durably scheduled.
        // Awaiting this task will wait for the task to complete.
        var depositTask = await customer.Deposit(120_000_000_000).ScheduleAsync("create-pc-industry");
        await depositTask.WaitAsync();

        var scheduledTask = await bank
            .Transfer(customer, business, 20)
            .ScheduleAsync("transfer-from-client");

        var success2 = await scheduledTask;
        Console.WriteLine(success2 ? "Success!" : "Fail :(");
        Console.WriteLine("Customer balance: " + await customer.GetBalance());
        Console.WriteLine("Business balance: " + await business.GetBalance());

        var clientGrain = client.GetGrain<IClientGrain>("client");
        await clientGrain.Run();

        Console.WriteLine("Now to do a similar thing via a grain workflow call");
        var workflowTask = await clientGrain.RunWorkflow().ScheduleAsync("my-client-wf");
        await workflowTask.WaitAsync();
    }

    [Alias("WorkflowsApp.Service.Samples.Bank.Bank.IBankGrain")]
    public interface IBankGrain : IGrainWithStringKey
    {
        [Alias("Transfer")]
        DurableTask<bool> Transfer(IAccountGrain source, IAccountGrain destination, long amount);
    }

    [Alias("IAccountGrain")]
    public interface IAccountGrain : IGrainWithStringKey
    {
        [Alias("Withdraw")]
        DurableTask<bool> Withdraw(long amount);

        [Alias("Deposit")]
        DurableTask Deposit(long amount);

        [Alias("GetBalance")]
        ValueTask<long> GetBalance();
    }

    public class BankGrain : DurableGrain, IBankGrain
    {
        public async DurableTask<bool> Transfer(
            IAccountGrain source,
            IAccountGrain destination,
            long amount)
        {
            var success = await source.Withdraw(amount).WithId("withdraw");
            if (!success) return false;

            await destination.Deposit(amount).WithId("deposit");
            return success;
        }
    }

    public class AccountGrain([FromKeyedServices("balance")] IDurableValue<long> balance) : DurableGrain, IAccountGrain
    {
        public async DurableTask Deposit(long amount)
        {
            await Task.CompletedTask;
            balance.Value += amount;
        }

        public async DurableTask<bool> Withdraw(long amount)
        {
            // To suppress CS1998.
            await Task.CompletedTask;

            // Atomically update the state and complete the task, ensuring idempotency.
            if (balance.Value >= amount)
            {
                balance.Value -= amount;
                return true;
            }

            return false;
        }

        public ValueTask<long> GetBalance() => new(balance.Value);
    }

    [Alias("IClientGrain")]
    public interface IClientGrain : IGrainWithStringKey
    {
        [Alias("Run")]
        Task Run();
        [Alias("RunWorkflow")]
        DurableTask RunWorkflow();
    }

    [GrainType("client")]
    [Alias("ClientGrain")]
    public class ClientGrain : DurableGrain, IClientGrain
    {
        public async Task Run()
        {
            var dictionary = GrainFactory.GetGrain<IDictionaryGrain<string, int>>("my-dict");
            var (added, version) = await dictionary.TryAddAsync("bananas", 12, 0);
            Console.WriteLine($"{added}, {version}");

            var bank = GrainFactory.GetGrain<IBankGrain>("bank");
            var customer = GrainFactory.GetGrain<IAccountGrain>("customer");
            var business = GrainFactory.GetGrain<IAccountGrain>("business");

            await customer.Deposit(120_000_000_000);

            // Schedule durable execution and return before completion.
            var scheduled = await customer.Deposit(120_000_000_000).ScheduleAsync();
            var id = scheduled.Id;
            Console.WriteLine(id);

            // Await completion of the scheduled task.
            await scheduled;

            var scheduledTransfer = await bank
                .Transfer(customer, business, 20)
                .WithId("transfer-from-grain")
                .ScheduleAsync();

            var success = await scheduledTransfer;
            Console.WriteLine(success ? "Success!" : "Fail :(");
            Console.WriteLine("Customer balance: " + await customer.GetBalance());
            Console.WriteLine("Business balance: " + await business.GetBalance());

            var scheduledTask2 = await bank
                .Transfer(customer, business, 20)
                .ScheduleAsync("transfer-from-grain-2");

            var success2 = await scheduledTask2;
            Console.WriteLine(success2 ? "Success!" : "Fail :(");
            Console.WriteLine("Customer balance: " + await customer.GetBalance());
            Console.WriteLine("Business balance: " + await business.GetBalance());

            IGrain[] grains = [bank, customer, business];
            foreach (var grain in grains)
            {
                Console.WriteLine($"Tasks for grain {grain}:");
                await foreach (var task in grain.Cast<IDurableTaskGrainExtension>().GetTasksAsync())
                {
                    Console.WriteLine($" * {task.TaskId}: {JsonConvert.SerializeObject(task.State, Formatting.Indented)}");
                }
            }
        }

        public async DurableTask RunWorkflow()
        {
            var client = GrainFactory;
            var bankGrain = client.GetGrain<IBankGrain>("bank");
            var customer = client.GetGrain<IAccountGrain>("customer");
            var business = client.GetGrain<IAccountGrain>("business");

            var randomId = await DurableTask.Run(ct => Guid.NewGuid()).WithId("generate-random-id");
            Console.WriteLine(randomId);

            // If the task is interrupted (eg, power outage) and is retried, it will only sleep for the remaining time.
            /*
            var slept = await DurableTask.Delay(TimeSpan.FromSeconds(1)).WithId("wait-for-confirmation");
            Console.WriteLine("slept? " + slept);
            */

            var scheduledTask = await bankGrain
                .Transfer(customer, business, 20)
                .ScheduleAsync("transfer-from-workflow");

            var success = await scheduledTask;
            Console.WriteLine(success ? "Success!" : "Fail :(");
            Console.WriteLine("Customer balance: " + await customer.GetBalance());
            Console.WriteLine("Business balance: " + await business.GetBalance());
        }
    }
}
