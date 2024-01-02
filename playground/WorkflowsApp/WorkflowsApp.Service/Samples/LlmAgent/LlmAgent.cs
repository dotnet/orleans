namespace WorkflowsApp.Service.Samples.LlmAgent;

public sealed class LlmAgent
{
    public Task RunAsync(IHost app)
    {
        // Human-in-the-loop agentic system
        // Human sends a task
        // Liaison agent receives the task,
        // Liaison agent plans how to implement the task.
        // Liaison agent sends sub-tasks to sub-agents.
        // When responses are received, liaison loops with sub-agents performing adjustments until satisfied.
        // Once the combined responses are satisfactory, the liaison agent sends the response to the human.

        return Task.CompletedTask;
    }

    internal interface ILiaisonAgent : IGrainWithStringKey
    {
    }

    internal class LiaisonAgent : DurableGrain, ILiaisonAgent
    {
    }
}
