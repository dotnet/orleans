using System;
using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;

namespace TestClient
{
    using Microsoft.Orleans.ServiceFabric.Client;

    using Orleans.Runtime;
    using Orleans.Runtime.Configuration;

    class Program
    {
        static void Main(string[] args)
        {
            var config = new ClientConfiguration
            {
                DataConnectionString = "UseDevelopmentStorage=true",
                PropagateActivityId = true,
                DefaultTraceLevel = Severity.Info,
                GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable,
                TraceToConsole = true,
                StatisticsCollectionLevel = StatisticsLevel.Critical,
                StatisticsLogWriteInterval = TimeSpan.FromDays(6),
                TraceFileName = null,
                TraceFilePattern = null,
                WriteMessagingTraces = false,
                ResponseTimeout = TimeSpan.FromSeconds(90),
                StatisticsMetricsTableWriteInterval = TimeSpan.FromDays(6),
                StatisticsPerfCountersWriteInterval = TimeSpan.FromDays(6),
            };
            OrleansFabricClient.Initialize(new Uri("fabric:/CalculatorApp/CalculatorService"), config);
            Run(args).Wait();
        }

        private static async Task Run(string[] args)
        {
            var calculator = GrainClient.GrainFactory.GetGrain<ICalculatorActor>(Guid.Empty);
            double result;
            if (args.Length < 1) {

                Console.WriteLine("Usage: TestClient.exe <operation> [operand]\n\tOperations: get, set, add, subtract, multiple, divide");
                return;
            }

            var op = args[0].ToLower();
            var value = args.Length > 1 ? double.Parse(args[1]) : 0;
                        
            switch (args[0].ToLower())
            {
                case "add":
                case "+":
                    result = await calculator.Add(value);
                    break;
                case "subtract":
                case "-":
                    result = await calculator.Subtract(value);
                    break;
                case "multiply":
                case "*":
                    result = await calculator.Multiply(value);
                    break;
                case "divide":
                case "/":
                    result = await calculator.Divide(value);
                    break;
                case "set":
                    result = await calculator.Set(value);
                    break;
                case "get":
                default:
                    result = await calculator.Get();
                    break;
            }

            Console.WriteLine(result);
        }
    }
}
