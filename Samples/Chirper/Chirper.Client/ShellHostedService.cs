using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Chirper.Grains;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace Chirper.Client
{
    public class ShellHostedService : BackgroundService
    {
        private readonly IClusterClient _client;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private ChirperConsoleViewer _viewer;
        private IChirperViewer _viewerRef;
        private IChirperAccount _account;

        public ShellHostedService(IClusterClient client, IHost host, IHostApplicationLifetime applicationLifetime)
        {
            _client = client;
            _applicationLifetime = applicationLifetime;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ShowHelp(true);

            while (!stoppingToken.IsCancellationRequested)
            {
                var command = Console.ReadLine();
                if (command == "/help")
                {
                    ShowHelp();
                }
                else if (command == "/quit")
                {
                    _applicationLifetime.StopApplication();
                    return;
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
                        if (_viewerRef == null)
                        {
                            _viewer = new ChirperConsoleViewer(_account.GetPrimaryKeyString());
                            _viewerRef = await _client.CreateObjectReference<IChirperViewer>(_viewer);
                        }

                        await _account.SubscribeAsync(_viewerRef);

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
            if (_viewerRef != null)
            {
                await _account.UnsubscribeAsync(_viewerRef);

                _viewerRef = null;
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
