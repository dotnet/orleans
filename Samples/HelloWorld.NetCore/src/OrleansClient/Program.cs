using HelloWorld.Interfaces;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OrleansClient
{
	/// <summary>
	/// Orleans test silo client
	/// </summary>
	public class Program
	{
		static int Main(string[] args)
		{
			var initializeClient = StartClient();
			initializeClient.Wait();
			var client = initializeClient.Result;

			DoClientWork(client).Wait();
			Console.WriteLine("Press Enter to terminate...");
			Console.ReadLine();
			return 0;
		}

		private static Task<IClusterClient> StartClient()
		{
			var config = ClientConfiguration.LocalhostSilo();

			try
			{
				return InitializeWithRetries(config);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Orleans client initialization failed failed due to {ex}");

				Console.ReadLine();
				return Task.FromException<IClusterClient>(ex);
			}
		}

		private static async Task<IClusterClient> InitializeWithRetries(ClientConfiguration config, int initializeAttemptsBeforeFailing = 5)
		{
			int attempt = 0;
			IClusterClient client;
			while (true)
			{
				try
				{
					client = new ClientBuilder()
						.UseConfiguration(config)
						.Build();

					await client.Connect();
					Console.WriteLine("Client successfully connect to silo host");
					break;
				}
				catch (SiloUnavailableException)
				{
					attempt++;
					Console.WriteLine($"Attempt {attempt} of {initializeAttemptsBeforeFailing} failed to initialize the Orleans client.");
					if (attempt > initializeAttemptsBeforeFailing)
					{
						throw;
					}
					Thread.Sleep(TimeSpan.FromSeconds(2));
				}
			}

			return client;
		}

		private static async Task DoClientWork(IClusterClient client)
		{
			// example of calling grains from the initialized client
			var friend = client.GetGrain<IHello>(0);
			var response = await friend.SayHello("Good morning, my friend!");
			Console.WriteLine("\n\n{0}\n\n", response);
		}

	}
}
