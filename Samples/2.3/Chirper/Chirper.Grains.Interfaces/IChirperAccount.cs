using System.Collections.Immutable;
using System.Threading.Tasks;
using Chirper.Grains.Models;

namespace Chirper.Grains
{
    /// <summary>
    /// Orleans grain interface IChirperAccount -- This is the user-facing control grain for a particular user. 
    /// A suitably authenticated external application can perform both publisher and subscriber functions on behalf of a user through this grain.
    /// This grain mediates command and communications between the external application and that user's publisher & subscriber agent grains.
    /// </summary>
    public interface IChirperAccount : IChirperPublisher, IChirperSubscriber
    {
        /// <summary>Add a new follower subscription from this user to the specified publisher</summary>
        /// <param name="userNameToFollow">The user name of the new publisher now being followed by this user</param>
        /// <returns>Task status for this operation</returns>
        Task FollowUserIdAsync(string userNameToFollow);

        /// <summary>Remove a follower subscription from this user to the specified publisher</summary>
        /// <param name="userNameToUnfollow">The user name of the publisher no longer being followed by this user</param>
        /// <returns>AsyncCompletion status for this operation</returns>
        Task UnfollowUserIdAsync(string userNameToUnfollow);

        /// <summary>Get the list of publishers who this user is following</summary>
        /// <returns>List of users being followed</returns>
        Task<ImmutableList<string>> GetFollowingListAsync();

        /// <summary>Get the list of subscribers who are following this user</summary>
        /// <returns>List of users who are following this user</returns>
        Task<ImmutableList<string>> GetFollowersListAsync();

        /// <summary>Publish a new Chirp message</summary>
        /// <param name="chirpMessage">The message text to be published as a new Chirp</param>
        /// <returns>Completion status for the publish operation</returns>
        Task PublishMessageAsync(string chirpMessage);

        /// <summary>Request the most recent 'n' Chirp messages received by this subscriber, from the specified start position.</summary>
        /// <param name="n">Number of Chirp messages requested. A value of -1 means all messages.</param>
        /// <param name="start">The start position for returned messages. A value of 0 means start with most recent message. A positive value means skip past that many of the most recent messages</param>
        /// <returns>List of Chirp messages received by this subscriber</returns>
        /// <remarks>The subscriber might only return a partial record of historic events due to message retention policies.</remarks>
        Task<ImmutableList<ChirperMessage>> GetReceivedMessagesAsync(int n = 10, int start = 0);

        /// <summary>
        /// Subscribes to real-time notifications from this grain.
        /// </summary>
        /// <param name="viewer">The observer to receive notifications.</param>
        Task SubscribeAsync(IChirperViewer viewer);

        /// <summary>
        /// Unsubscribes the given viewer from real-time notifications from this grain.
        /// </summary>
        /// <param name="viewer"></param>
        Task UnsubscribeAsync(IChirperViewer viewer);
    }
}
