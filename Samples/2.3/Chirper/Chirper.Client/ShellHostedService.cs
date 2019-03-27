using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Chirper.Grains;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace Chirper.Client
{
    public class ShellHostedService : IHostedService
    {
        private readonly IClusterClient _client;
        private readonly IHost _host;
        private IChirperViewer _viewer;
        private IChirperAccount _account;
        private Task _execution;

        public ShellHostedService(IClusterClient client, IHost host)
        {
            _client = client;
            _host = host;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _execution = RunAsync();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // as we cannot stop the console by graceful means, there is nothing to do
            // the host itself will stop the console when it terminates the application
            return Task.CompletedTask;
        }

        public async Task RunAsync()
        {
            ShowHelp(true);

            while (true)
            {
                var command = Console.ReadLine();
                if (command == "/help")
                {
                    ShowHelp();
                }
                else if (command == "/quit")
                {
                    await _host.StopAsync();
                }
                else if (command.StartsWith("/user "))
                {
                    var match = Regex.Match(command, @"/user (?<username>\w{1,100})");
                    if (match.Success)
                    {
                        await Unobserve();
                        var username = match.Groups["username"].Value;
                        _account = _client.GetGrain<IChirperAccount>(username);

                        Console.WriteLine($"The current user is now [{username}]");
                    }
                    else
                    {
                        Console.WriteLine("Invalid username. Try again or type /help for a list of commands.");
                    }
                }
                else if (command.StartsWith("/follow "))
                {
                    if (EnsureActiveAccount())
                    {
                        var match = Regex.Match(command, @"/follow (?<username>\w{1,100})");
                        if (match.Success)
                        {
                            var targetName = match.Groups["username"].Value;
                            await _account.FollowUserIdAsync(targetName);

                            Console.WriteLine($"[{_account.GetPrimaryKeyString()}] is now following [{targetName}]");
                        }
                        else
                        {
                            Console.WriteLine("Invalid target username. Try again or type /help for a list of commands.");
                        }
                    }
                }
                else if (command == "/following")
                {
                    if (EnsureActiveAccount())
                    {
                        (await _account.GetFollowingListAsync())
                            .ForEach(_ => Console.WriteLine(_));
                    }
                }
                else if (command == "/followers")
                {
                    if (EnsureActiveAccount())
                    {
                        (await _account.GetFollowersListAsync())
                            .ForEach(_ => Console.WriteLine(_));
                    }
                }
                else if (command == "/observe")
                {
                    if (EnsureActiveAccount())
                    {
                        if (_viewer == null)
                        {
                            _viewer = await _client.CreateObjectReference<IChirperViewer>(new ChirperConsoleViewer(_account.GetPrimaryKeyString()));
                        }

                        await _account.SubscribeAsync(_viewer);

                        Console.WriteLine($"Now observing [{_account.GetPrimaryKeyString()}]");
                    }
                }
                else if (command == "/unobserve")
                {
                    if (EnsureActiveAccount())
                    {
                        await Unobserve();
                    }
                }
                else if (command.StartsWith("/unfollow "))
                {
                    if (EnsureActiveAccount())
                    {
                        var match = Regex.Match(command, @"/unfollow (?<username>\w{1,100})");
                        if (match.Success)
                        {
                            var targetName = match.Groups["username"].Value;
                            await _account.UnfollowUserIdAsync(targetName);

                            Console.WriteLine($"[{_account.GetPrimaryKeyString()}] is no longer following [{targetName}]");
                        }
                        else
                        {
                            Console.WriteLine("Invalid target username. Try again or type /help for a list of commands.");
                        }
                    }
                }
                else if (command.StartsWith("/chirp "))
                {
                    if (EnsureActiveAccount())
                    {
                        var match = Regex.Match(command, @"/chirp (?<message>.+)");
                        if (match.Success)
                        {
                            var message = match.Groups["message"].Value;
                            await _account.PublishMessageAsync(message);
                            Console.WriteLine("Published the new message!");
                        }
                        else
                        {
                            Console.WriteLine("Invalid chirp. Try again or type /help for a list of commands.");
                        }
                    }
                }
                else
                {
                    Console.WriteLine("Unknown command. Type /help for list of commands.");
                }
            }
        }

        private bool EnsureActiveAccount()
        {
            if (_account == null)
            {
                Console.WriteLine("This command requires an active user. Try again or type /help for a list of commands.");
                return false;
            }
            return true;
        }

        private async Task Unobserve()
        {
            if (_viewer != null)
            {
                await _account.UnsubscribeAsync(_viewer);

                _viewer = null;

                Console.WriteLine($"No longer observing [{_account.GetPrimaryKeyString()}]");
            }
        }

        private void ShowHelp(bool title = false)
        {
            if (title)
            {
                Console.WriteLine();
                Console.WriteLine("Welcome to the Chirper Sample!");
                Console.WriteLine("These are the available commands:");
            }

            Console.WriteLine("/help: Shows this list.");
            Console.WriteLine("/user <username>: Acts as the given account.");
            Console.WriteLine("/chirp <message>: Publishes a message to the active account.");
            Console.WriteLine("/follow <username>: Makes the active account follows the given account.");
            Console.WriteLine("/unfollow <username>: Makes the active account unfollow the given accout.");
            Console.WriteLine("/following: Lists the accounts that the active account is following.");
            Console.WriteLine("/followers: Lists the accounts that follow the active account.");
            Console.WriteLine("/observe: Start receiving real-time activity updates from the active account.");
            Console.WriteLine("/unobserve: Stop receiving real-time activity updates from the active account.");
            Console.WriteLine("/quit: Closes this client.");
            Console.WriteLine();
        }
    }
}
