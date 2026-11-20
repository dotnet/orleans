namespace Documentation.Diagnostics.DurableTasks;

public interface ICompletionSurveyGrain : IGrainWithStringKey
{
    DurableTask CompleteAsync(string customer);

    DurableTask CompleteAndSurveyAsync(string customer);

    DurableTask CompleteThenSurveyAsync(string customer);
}

public sealed class CompletionSurveyGrain : DurableGrain, ICompletionSurveyGrain
{
#pragma warning disable ORLEANS0228
    // <orleans0228_violation>
    public async DurableTask CompleteAsync(string customer)
    {
        await Pack().WithId("pack");

        // A local method runs as part of this execution, so starting it detached throws InvalidOperationException.
        _ = SurveyAsync(customer).WithId("survey").Detached().Post();
    }
    // </orleans0228_violation>
#pragma warning restore ORLEANS0228

    // <orleans0228_fix_grain>
    public async DurableTask CompleteAndSurveyAsync(string customer)
    {
        await Pack().WithId("pack");

        // The surveys grain stores and runs the call as a root task, which continues after this execution completes.
        _ = GrainFactory.GetGrain<ICustomerSurveys>(customer).SendSurveyAsync(customer).WithId("survey").Detached().Post();
    }
    // </orleans0228_fix_grain>

    // <orleans0228_fix_attached>
    public async DurableTask CompleteThenSurveyAsync(string customer)
    {
        await Pack().WithId("pack");

        // The step runs as a child of this execution, which waits for it.
        await SurveyAsync(customer).WithId("survey");
    }
    // </orleans0228_fix_attached>

    private static async DurableTask<bool> Pack() => true;

    private static async DurableTask<bool> SurveyAsync(string customer) => customer.Length > 0;
}
