using System.Collections.Immutable;
using System.Threading.Tasks;
using Chirper.Grains.Models;
using Orleans;

namespace Chirper.Grains
{
    /// <summary>
    /// Orleans grain interface IChirperPublisher.
    /// </summary>
    public interface IChirperPublisher : IGrainWithStringKey
    {
        /// <summary>
        /// Request a copy of the most recent 'n' Chirp messages posted by this publisher, from the specified start position.
        /// </summary>
        /// <param name="n">Number of Chirp messages requested. A value of -1 means all messages.</param>
        /// <param name="start">The start position for returned messages. A value of 0 means start with most recent message. A positive value means skip past that many of the most recent messages</param>
        /// <returns>Bulk list of Chirp messages posted by this publisher</returns>
        /// <remarks>The publisher might only return a partial record of historic events due to message retention policies.</remarks>
        Task<ImmutableList<ChirperMessage>> GetPublishedMessagesAsync(int n = 10, int start = 0);

        /// <summary>
        /// Subscribe from receiving notifications of new Chirps sent by this publisher.
        /// </summary>
        /// <param name="userName">The user name of the subscriber to add.</param>
        /// <param name="subscriber">The subscriber to add.</param>
        /// <returns>AsyncCompletion status for this operation</returns>
        Task AddFollowerAsync(string userName, IChirperSubscriber subscriber);

        /// <summary>
        /// Unsubscribe from receiving notifications of new Chirps sent by this publisher.
        /// </summary>
        /// <param name="userName">The user name of the subscriber to remove.</param>
        /// <returns>AsyncCompletion for this operation</returns>
        Task RemoveFollowerAsync(string userName);
    }
}
