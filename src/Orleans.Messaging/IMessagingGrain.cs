namespace Orleans.Messaging;

/// <summary>
/// Identifies grain implementations which initialize inbox and outbox services during activation setup.
/// </summary>
/// <remarks>
/// Implement this local capability on a grain class, an application base class, or an application grain interface.
/// With messaging services registered, selected activations validate their execution model and bind
/// messaging state before journal recovery. Grains deriving from <see cref="Orleans.Journaling.DurableGrain"/>
/// receive the same setup automatically.
/// </remarks>
public interface IMessagingGrain
{
}
