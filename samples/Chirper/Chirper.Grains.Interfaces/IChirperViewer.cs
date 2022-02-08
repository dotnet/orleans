using Chirper.Grains.Models;
using Orleans;

namespace Chirper.Grains;

/// <summary>
/// Interface of observers of an <see cref="IChirperAccount"/> instance.
/// </summary>
public interface IChirperViewer : IGrainObserver
{
    /// <summary>
    /// Notifies that an account has published the message.
    /// This is either the observed account or a followed account.
    /// </summary>
    void NewChirp(ChirperMessage message);

    /// <summary>
    /// Notifies that the account has added a subscription.
    /// </summary>
    void SubscriptionAdded(string username);

    /// <summary>
    /// Notifies that the account has removed a subscription.
    /// </summary>
    void SubscriptionRemoved(string username);

    void NewFollower(string username);
}
