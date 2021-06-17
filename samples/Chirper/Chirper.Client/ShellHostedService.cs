using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Chirper.Grains;
using Microsoft.Extensions.Hosting;
using Orleans;
using Spectre.Console;

namespace Chirper.Client
{
    public class ShellHostedService : BackgroundService
    {
        private readonly IClusterClient _client;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private ChirperConsoleViewer _viewer;
        private IChirperViewer _viewerRef;
        private IChirperAccount _account;

        public ShellHostedService(IClusterClient client, IHostApplicationLifetime applicationLifetime)
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
                else if (command is null || command == "/quit")
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

                        AnsiConsole.MarkupLine("[bold grey][[[/][bold lime]✓[/][bold grey]]][/] The current user is now [navy]{0}[/]", username);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[bold red]Invalid username[/][red].[/] Try again or type [bold fuchsia]/help[/] for a list of commands.");
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
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[bold grey][[[/][bold red]✗[/][bold grey]]][/] [red underline]Invalid target username[/][red].[/] Try again or type [bold fuchsia]/help[/] for a list of commands.");
                        }
                    }
                }
                else if (command == "/following")
                {
                    if (EnsureActiveAccount())
                    {
                        var following = await _account.GetFollowingListAsync();
                        AnsiConsole.Render(new Rule($"{_account.GetPrimaryKeyString()}'s followed accounts")
                        {
                            Alignment = Justify.Center,
                            Style = Style.Parse("blue")
                        });

                        foreach (var account in following)
                        {
                            AnsiConsole.MarkupLine("[bold yellow]{0}[/]", account);
                        }

                        AnsiConsole.Render(new Rule()
                        {
                            Alignment = Justify.Center,
                            Style = Style.Parse("blue")
                        });
                    }
                }
                else if (command == "/followers")
                {
                    if (EnsureActiveAccount())
                    {
                        var followers = await _account.GetFollowersListAsync();
                        AnsiConsole.Render(new Rule($"{_account.GetPrimaryKeyString()}'s followers")
                        {
                            Alignment = Justify.Center,
                            Style = Style.Parse("blue")
                        });

                        foreach (var account in followers)
                        {
                            AnsiConsole.MarkupLine("[bold yellow]{0}[/]", account);
                        }

                        AnsiConsole.Render(new Rule()
                        {
                            Alignment = Justify.Center,
                            Style = Style.Parse("blue")
                        });
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

                        AnsiConsole.MarkupLine("[bold grey][[[/][bold lime]✓[/][bold grey]]][/] [bold olive]Now observing[/] [navy]{0}[/]", _account.GetPrimaryKeyString());
                    }
                }
                else if (command == "/unobserve")
                {
                    if (EnsureActiveAccount())
                    {
                        await Unobserve();
                        AnsiConsole.MarkupLine("[bold grey][[[/][bold lime]✓[/][bold grey]]][/] [bold olive]No longer observing[/] [navy]{0}[/]", _account.GetPrimaryKeyString());
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
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[bold grey][[[/][bold red]✗[/][bold grey]]][/] [red underline]Invalid target username[/][red].[/] Try again or type [bold fuchsia]/help[/] for a list of commands.");
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
                            AnsiConsole.MarkupLine("[bold grey][[[/][bold lime]✓[/][bold grey]]][/] Published a new message!");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[bold grey][[[/][bold red]✗[/][bold grey]]][/] [red underline]Invalid chirp[/][red].[/] Try again or type [bold fuchsia]/help[/] for a list of commands.");
                        }
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[bold grey][[[/][bold red]✗[/][bold grey]]][/] [red underline]Unknown command[/][red].[/] Type [bold fuchsia]/help[/] for a list of commands.");
                }
            }
        }

        private bool EnsureActiveAccount()
        {
            if (_account == null)
            {
                AnsiConsole.MarkupLine("[bold grey][[[/][bold red]✗[/][bold grey]]][/] This command requires an [red underline]active user[/][red].[/]"
                    + " Set an active user using [bold fuchsia]/fuchsia[/] [aqua]username[/] or type [bold fuchisa]/help] for a list of commands.");
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
            }
        }

        private void ShowHelp(bool title = false)
        {
            var markup = new Markup(
                "[bold fuchsia]/help[/]: Shows this [underline green]help[/] text.\n"
                + "[bold fuchsia]/user[/] [aqua]<username>[/]: Switches to the specified [underline green]user[/] account.\n"
                + "[bold fuchsia]/chirp[/] [aqua]<message>[/]: [underline green]Chirps[/] a [aqua]message[/] from the active account.\n"
                + "[bold fuchsia]/follow[/] [aqua]<username>[/]: [underline green]Follows[/] the account with the specified [aqua]username[/].\n"
                + "[bold fuchsia]/unfollow[/] [aqua]<username>[/]: [underline green]Unfollows[/] the account with the specified [aqua]username[/].\n"
                + "[bold fuchsia]/following[/]: Lists the accounts that the active account is [underline green]following[/].\n"
                + "[bold fuchsia]/followers[/]: Lists the accounts [underline green]followers[/] the active account.\n"
                + "[bold fuchsia]/observe[/]: [underline green]Start observing[/] the active account.\n"
                + "[bold fuchsia]/unobserve[/]: [underline green]Stop observing[/] the active account.\n"
                + "[bold fuchsia]/quit[/]: Closes this client.\n");
            if (title)
            {
                // Add some flair for the title screen
                using var logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Chirper.Client.logo.png");
                var logo = new CanvasImage(logoStream)
                {
                    MaxWidth = 25
                };

                var table = new Table()
                {
                    Border = TableBorder.None,
                    Expand = true,
                }.HideHeaders();
                table.AddColumn(new TableColumn("One"));

                var header = new FigletText("Orleans")
                {
                    Color = Color.Fuchsia
                };
                var header2 = new FigletText("Chirper")
                {
                    Color = Color.Aqua
                };

                table.AddColumn(new TableColumn("Two"));
                var rightTable = new Table().HideHeaders().Border(TableBorder.None).AddColumn(new TableColumn("Content"));
                rightTable.AddRow(header).AddRow(header2).AddEmptyRow().AddEmptyRow().AddRow(markup);
                table.AddRow(logo, rightTable);

                AnsiConsole.WriteLine();
                AnsiConsole.Render(table);
                AnsiConsole.WriteLine();
            }
            else
            {
                AnsiConsole.Render(markup);
            }
        }
    }
}
