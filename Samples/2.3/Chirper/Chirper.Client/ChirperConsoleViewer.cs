using System;
using Chirper.Grains;
using Chirper.Grains.Models;

namespace Chirper.Client
{
    /// <summary>
    /// Implements an <see cref="IChirperViewer"/> that outputs notifications to the console.
    /// </summary>
    public class ChirperConsoleViewer : IChirperViewer
    {
        /// <summary>
        /// Creates a new <see cref="IChirperViewer"/> that outputs notifications to the console.
        /// </summary>
        /// <param name="userName">The user name of the account being observed.</param>
        public ChirperConsoleViewer(string userName)
        {
            this.userName = userName ?? throw new ArgumentNullException(nameof(userName));
        }

        /// <summary>
        /// The user name of the account being observed.
        /// </summary>
        private readonly string userName;

        /// <inheritdoc />
        public void NewChirp(ChirperMessage message)
        {
            Console.WriteLine($"Observed: Account {message.PublisherUserName} chirped '{message.Message}'");
        }

        /// <inheritdoc />
        public void SubscriptionAdded(string username)
        {
            Console.WriteLine($"Observed: [{this.userName}] is now following [{username}]");
        }

        /// <inheritdoc />
        public void SubscriptionRemoved(string username)
        {
            Console.WriteLine($"Observed: [{this.userName}] is no longer following [{username}]");
        }
    }
}
