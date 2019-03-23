using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Chirper.Grains;
using Orleans;

namespace Chirper.Client
{
    public class Shell
    {
        public Shell(IClusterClient client)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
        }

        private readonly IClusterClient client;
        private IChirperViewer viewer;
        private IChirperAccount account;

        public async Task RunAsync(IClusterClient client)
        {
            this.ShowHelp(true);

            while (true)
            {
                var command = Console.ReadLine();
                if (command == "/help")
                {
                    this.ShowHelp();
                }
                else if (command == "/quit")
                {
                    return;
                }
                else if (command.StartsWith("/user "))
                {
                    var match = Regex.Match(command, @"/user (?<username>\w{1,100})");
                    if (match.Success)
                    {
                        await this.Unobserve();
                        var username = match.Groups["username"].Value;
                        this.account = client.GetGrain<IChirperAccount>(username);

                        Console.WriteLine($"The current user is now [{username}]");
                    }
                    else
                    {
                        Console.WriteLine("Invalid username. Try again or type /help for a list of commands.");
                    }
                }
                else if (command.StartsWith("/follow "))
                {
                    if (this.EnsureActiveAccount())
                    {
                        var match = Regex.Match(command, @"/follow (?<username>\w{1,100})");
                        if (match.Success)
                        {
                            var targetName = match.Groups["username"].Value;
                            await this.account.FollowUserIdAsync(targetName);

                            Console.WriteLine($"[{this.account.GetPrimaryKeyString()}] is now following [{targetName}]");
                        }
                        else
                        {
                            Console.WriteLine("Invalid target username. Try again or type /help for a list of commands.");
                        }
                    }
                }
                else if (command == "/following")
                {
                    if (this.EnsureActiveAccount())
                    {
                        (await this.account.GetFollowingListAsync())
                            .ForEach(_ => Console.WriteLine(_));
                    }
                }
                else if (command == "/followers")
                {
                    if (this.EnsureActiveAccount())
                    {
                        (await this.account.GetFollowersListAsync())
                            .ForEach(_ => Console.WriteLine(_));
                    }
                }
                else if (command == "/observe")
                {
                    if (this.EnsureActiveAccount())
                    {
                        if (this.viewer == null)
                        {
                            this.viewer = await client.CreateObjectReference<IChirperViewer>(new ChirperConsoleViewer(this.account.GetPrimaryKeyString()));
                        }

                        await this.account.SubscribeAsync(this.viewer);

                        Console.WriteLine($"Now observing [{this.account.GetPrimaryKeyString()}]");
                    }
                }
                else if (command == "/unobserve")
                {
                    if (this.EnsureActiveAccount())
                    {
                        await this.Unobserve();
                    }
                }
                else if (command.StartsWith("/unfollow "))
                {
                    if (this.EnsureActiveAccount())
                    {
                        var match = Regex.Match(command, @"/unfollow (?<username>\w{1,100})");
                        if (match.Success)
                        {
                            var targetName = match.Groups["username"].Value;
                            await this.account.UnfollowUserIdAsync(targetName);

                            Console.WriteLine($"[{this.account.GetPrimaryKeyString()}] is no longer following [{targetName}]");
                        }
                        else
                        {
                            Console.WriteLine("Invalid target username. Try again or type /help for a list of commands.");
                        }
                    }
                }
                else if (command.StartsWith("/chirp "))
                {
                    if (this.EnsureActiveAccount())
                    {
                        var match = Regex.Match(command, @"/chirp (?<message>.+)");
                        if (match.Success)
                        {
                            var message = match.Groups["message"].Value;
                            await this.account.PublishMessageAsync(message);
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
            if (this.account == null)
            {
                Console.WriteLine("This command requires an active user. Try again or type /help for a list of commands.");
                return false;
            }
            return true;
        }

        private async Task Unobserve()
        {
            if (this.viewer != null)
            {
                await this.account.UnsubscribeAsync(this.viewer);

                this.viewer = null;

                Console.WriteLine($"No longer observing [{this.account.GetPrimaryKeyString()}]");
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
