using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

internal interface IActivationDeactivationParticipant
{
    void OnDeactivationRequested();

    Task OnDeactivatingAsync(CancellationToken cancellationToken);
}

internal sealed class ActivationDeactivationCoordinator : IActivationDeactivationParticipant
{
    private readonly List<IActivationDeactivationParticipant> _participants = [];

    public static void Register(IGrainContext context, IActivationDeactivationParticipant participant)
    {
        var coordinator = context.GetComponent<ActivationDeactivationCoordinator>();
        if (coordinator is null)
        {
            coordinator = new ActivationDeactivationCoordinator();
            context.SetComponent(coordinator);
            context.SetComponent<IActivationDeactivationParticipant>(coordinator);
        }

        coordinator._participants.Add(participant);
    }

    public void OnDeactivationRequested()
    {
        foreach (var participant in _participants)
        {
            participant.OnDeactivationRequested();
        }
    }

    public async Task OnDeactivatingAsync(CancellationToken cancellationToken)
    {
        List<Exception>? exceptions = null;
        foreach (var participant in _participants)
        {
            try
            {
                await participant.OnDeactivatingAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is not null)
        {
            throw new AggregateException("One or more activation deactivation participants failed.", exceptions);
        }
    }
}
