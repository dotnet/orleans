using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GrainInterfaces;
using GrainInterfaces.Model;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;
using Utils;

namespace OrleansClient
{
	class Program
	{
        //To make this sample simple
        //In this sample, one client can only join one channel, hence we have a static variable of one channel name.
        //client can send messages to the channel , and receive messages sent to the channel/stream from other clients. 
		private static string joinedChannel = "general";
        private static string userName = "UserWithNoName";
		public static void Main(string[] args)
		{
			var clientInstance = InitializeClient(args).GetAwaiter().GetResult();

			PrettyConsole.Line("==== CLIENT: Initialized ====", ConsoleColor.Cyan);
			PrettyConsole.Line("CLIENT: Write commands:", ConsoleColor.Cyan);


			Menu(clientInstance).GetAwaiter().GetResult();

			PrettyConsole.Line("==== CLIENT: Shutting down ====", ConsoleColor.DarkRed);
		}

		private static async Task<IClusterClient> InitializeClient(string[] args)
		{
			int initializeCounter = 0;

			var initSucceed = false;
			while (!initSucceed)
			{
				try
				{
                    var client = new ClientBuilder().Configure<ClusterOptions>(options =>
                        {
                            options.ClusterId = Constants.ClusterId;
                            options.ServiceId = Constants.ServiceId;
                        })
                        .UseLocalhostClustering()
                        .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IChannel).Assembly).WithReferences())
				        .ConfigureLogging(logging => logging.AddConsole())
                        .AddSimpleMessageStreamProvider(Constants.ChatRoomStreamProvider)
				        .Build();
                    await client.Connect();
					initSucceed = client.IsInitialized;

					if (initSucceed)
					{
						return client;
					}
				}
				catch (Exception exc)
				{
					PrettyConsole.Line(exc.Message, ConsoleColor.Cyan);
					initSucceed = false;
				}

				if (initializeCounter++ > 10)
				{
					return null;
				}

				PrettyConsole.Line("Client Init Failed. Sleeping 5s...", ConsoleColor.Red);
				Thread.Sleep(TimeSpan.FromSeconds(5));
			}

			return null;
		}

		private static void PrintHints()
		{
			var menuColor = ConsoleColor.Magenta;
			PrettyConsole.Line("Type '/j <channel>' to join specific channel", menuColor);
		    PrettyConsole.Line("Type '/n <username>' to set your user name", menuColor);
            PrettyConsole.Line("Type '/l' to leave specific channel", menuColor);
			PrettyConsole.Line("Type '<any text>' to send a message", menuColor);
		    PrettyConsole.Line("Type '/h' to re-read channel history", menuColor);
		    PrettyConsole.Line("Type '/m' to query members in the channel", menuColor);
            PrettyConsole.Line("Type '/exit' to exit client.", menuColor);
		}

		private static async Task Menu(IClusterClient client)
		{
			string input;
			PrintHints();

			do
			{
				input = Console.ReadLine();

				if (string.IsNullOrWhiteSpace(input)) continue;

				if (input.StartsWith("/j"))
				{
					await JoinChannel(client, input.Replace("/j", "").Trim());
				}
                else if (input.StartsWith("/n"))
				{
				    userName = input.Replace("/n", "").Trim();
				    PrettyConsole.Line($"Your user name is set to be {userName}", ConsoleColor.DarkGreen);
                }
				else if (input.StartsWith("/l"))
				{
					await LeaveChannel(client);
				}
				else if (input.StartsWith("/h"))
				{
				    await ShowCurrentChannelHistory(client);
				}
				else if (input.StartsWith("/m"))
				{
				    await ShowChannelMembers(client);
				}
                else if (!input.StartsWith("/exit"))
				{
					await SendMessage(client, input);
				}
			} while (input != "/exit");
		}

	    private static async Task ShowChannelMembers(IClusterClient client)
	    {
	        var room = client.GetGrain<IChannel>(joinedChannel);
	        var members = await room.GetMembers();

	        PrettyConsole.Line($"====== Members for '{joinedChannel}' Channel ======", ConsoleColor.DarkGreen);
	        foreach (var member in members)
	        {
	            PrettyConsole.Line(member, ConsoleColor.DarkGreen);
	        }
	        PrettyConsole.Line("============", ConsoleColor.DarkGreen);
	    }

        private static async Task ShowCurrentChannelHistory(IClusterClient client)
	    {
	        var room = client.GetGrain<IChannel>(joinedChannel);
	        var history = await room.ReadHistory(1000);

	        PrettyConsole.Line($"====== History for '{joinedChannel}' Channel ======", ConsoleColor.DarkGreen);
	        foreach (var chatMsg in history)
	        {
	            PrettyConsole.Line($" ({chatMsg.Created:g}) {chatMsg.Author}> {chatMsg.Text}", ConsoleColor.DarkGreen);
	        }
	        PrettyConsole.Line("============", ConsoleColor.DarkGreen);
	    }

        private static async Task SendMessage(IClusterClient client, string messageText)
		{
			var room = client.GetGrain<IChannel>(joinedChannel);
			await room.Message(new ChatMsg(userName, messageText));
		}

		private static async Task JoinChannel(IClusterClient client, string channelName)
		{
		    PrettyConsole.Line($"Joining to channel {channelName}");
            joinedChannel = channelName;
			var room = client.GetGrain<IChannel>(joinedChannel);
		    var streamId = await room.Join(userName);
            //empty stream id meaning client already joined this channel, so return 
		    if (streamId == Guid.Empty) return;
            var stream = client.GetStreamProvider(Constants.ChatRoomStreamProvider)
		        .GetStream<ChatMsg>(streamId, Constants.CharRoomStreamNameSpace);
            //sunscribe to the stream to receiver furthur messages sent to the chatroom
		    await stream.SubscribeAsync(new StreamObserver(client.ServiceProvider.GetService<ILoggerFactory>()
		        .CreateLogger($"{joinedChannel} channel")));
		}

		private static async Task LeaveChannel(IClusterClient client)
		{
		    PrettyConsole.Line($"Leaving to channel {joinedChannel}");
            var room = client.GetGrain<IChannel>(joinedChannel);
		    var streamId = await room.Leave(userName);
            //empty stream id meaning client already leaved the channel, so return 
		    if (streamId == Guid.Empty) return;
            var stream = client.GetStreamProvider(Constants.ChatRoomStreamProvider)
		        .GetStream<ChatMsg>(streamId, Constants.CharRoomStreamNameSpace);
            //unsubscribe from the channel/stream since client left, so that client won't
            //receive furture messages from this channel/stream
		    var subscriptionHandles = await stream.GetAllSubscriptionHandles();
		    foreach (var handle in subscriptionHandles)
		    {
		        await handle.UnsubscribeAsync();
		    }
		}
	}
}