namespace Documentation.Diagnostics.DurableTasks;

public sealed class EnrollmentGrain : DurableGrain
{
    private static readonly DurableVersion Welcomes = new("welcomes", minSupported: 0, maxSupported: 1);

    private readonly IDurableValue<int> _enrolled;

    public EnrollmentGrain() => _enrolled = StateManager.GetOrAddValue<int>("enrolled");

#pragma warning disable ORLEANS0227
    // <orleans0227_violation>
    public async DurableTask<int> EnrollAsync(string student)
    {
        var seat = DurableTask.RunInline("seat", () =>
        {
            var number = ++_enrolled.Value;

            // The version check throws InvalidOperationException, which the effect records as its outcome.
            if (Welcomes.IsAtLeast(1))
            {
                _ = Welcome(student, number).WithId("welcome").Detached().Post();
            }

            return number;
        });

        await Confirm(student, seat).WithId("confirm");
        return seat;
    }
    // </orleans0227_violation>
#pragma warning restore ORLEANS0227

    // <orleans0227_fix>
    public async DurableTask<int> EnrollAndWelcomeAsync(string student)
    {
        // The effect returns the seat, and the body checks the version and starts the welcome.
        var seat = DurableTask.RunInline("seat", () => ++_enrolled.Value);
        if (Welcomes.IsAtLeast(1))
        {
            _ = Welcome(student, seat).WithId("welcome").Detached().Post();
        }

        await Confirm(student, seat).WithId("confirm");
        return seat;
    }
    // </orleans0227_fix>

    private static async DurableTask<bool> Welcome(string student, int seat) => student.Length > 0 && seat > 0;

    private static async DurableTask<bool> Confirm(string student, int seat) => student.Length > 0 && seat > 0;
}
