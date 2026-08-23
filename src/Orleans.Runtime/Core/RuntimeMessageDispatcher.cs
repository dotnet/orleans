namespace Orleans.Runtime;

internal static class RuntimeMessageDispatcher
{
    public static void Dispatch(IGrainContext context, Message message)
    {
        switch (context)
        {
            case ActivationData activation:
                activation.ReceiveMessage(message);
                break;
            case StatelessWorkerGrainContext statelessWorker:
                statelessWorker.ReceiveMessage(message);
                break;
            case HostedClient hostedClient:
                hostedClient.ReceiveMessage(message);
                break;
            case SystemTarget systemTarget:
                systemTarget.ReceiveMessage(message);
                break;
            default:
                context.ReceiveMessage(message);
                break;
        }
    }
}
