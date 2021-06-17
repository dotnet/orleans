using System;
using Chirper.Grains;
using Chirper.Grains.Models;
using Spectre.Console;

namespace Chirper.Client
{
    /// <summary>
    /// Implements an <see cref="IChirperViewer"/> that outputs notifications to the console.
    /// </summary>
    public class ChirperConsoleViewer : IChirperViewer
    {
        /// <summary>
        /// The user name of the account being observed.
        /// </summary>
        private readonly string _userName;

        /// <summary>
        /// Creates a new <see cref="IChirperViewer"/> that outputs notifications to the console.
        /// </summary>
        /// <param name="userName">The user name of the account being observed.</param>
        public ChirperConsoleViewer(string userName)
        {
            _userName = userName ?? throw new ArgumentNullException(nameof(userName));
        }

        /// <inheritdoc />
        public void NewChirp(ChirperMessage message)
        {
            AnsiConsole.MarkupLine("[[[dim]{0}[/]]] [aqua]{1}[/] [bold yellow]chirped:[/] {2}", message.Timestamp.LocalDateTime, message.PublisherUserName, message.Message);
        }

        public void NewFollower(string username)
        {
            AnsiConsole.MarkupLine("[bold grey][[[/][bold yellow]![/][bold grey]]][/] [aqua]{0}[/] is now following [navy]{1}[/]", username, _userName);
        }

        /// <inheritdoc />
        public void SubscriptionAdded(string username)
        {
            AnsiConsole.MarkupLine("[bold grey][[[/][bold lime]✓[/][bold grey]]][/] [navy]{0}[/] is now following [aqua]{1}[/]", _userName, username);
        }

        /// <inheritdoc />
        public void SubscriptionRemoved(string username)
        {
            AnsiConsole.MarkupLine("[bold grey][[[/][bold lime]✓[/][bold grey]]][/] [navy]{0}[/] is no longer following [aqua]{1}[/]", _userName, username);
        }
    }
}
