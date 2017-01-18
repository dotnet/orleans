using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GrainInterfaces;
using Microsoft.Orleans.ServiceFabric;
using Orleans;
using Orleans.Runtime.Configuration;

namespace StatelessCalculatorClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var serviceName = new Uri("fabric:/StatelessCalculatorApp/StatelessCalculatorService");

            var config = new ClientConfiguration
            {
                DeploymentId = Regex.Replace(serviceName.PathAndQuery.Trim('/'), "[^a-zA-Z0-9_]", "_"),
                GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable,
                DataConnectionString = "UseDevelopmentStorage=true"
            };
            GrainClient.Initialize(config);
            Run(args).Wait();
        }

        private static async Task Run(string[] args)
        {
            var calculator = GrainClient.GrainFactory.GetGrain<ICalculatorGrain>(Guid.Empty);
            double result;
            if (args.Length < 1)
            {

                Console.WriteLine($"Usage: {Assembly.GetExecutingAssembly()} <operation> [operand]\n\tOperations: get, set, add, subtract, multiple, divide");
                return;
            }

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