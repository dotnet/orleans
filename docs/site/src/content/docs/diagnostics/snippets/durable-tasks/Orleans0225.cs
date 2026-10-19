namespace Documentation.Diagnostics.DurableTasks;

public sealed class DatasetGrain : DurableGrain
{
#pragma warning disable ORLEANS0225
    // <orleans0225_violation>
    public async DurableTask<string> PublishAsync(string dataset)
    {
        var export = await Export(dataset).WithId("export").ScheduleAsync();

        // The poll throws NotSupportedException in a durable task body.
        while (!await export.IsCompletedAsync())
        {
            await DurableTask.Delay(TimeSpan.FromMinutes(1)).WithId("poll-interval");
        }

        return await export;
    }
    // </orleans0225_violation>
#pragma warning restore ORLEANS0225

    // <orleans0225_fix_await>
    public async DurableTask<string> PublishWhenExportedAsync(string dataset)
    {
        // The body resumes when the export completes, and a replay returns its recorded result.
        return await Export(dataset).WithId("export");
    }
    // </orleans0225_fix_await>

    // <orleans0225_fix_timeout>
    public async DurableTask<string?> PublishWithinAsync(string dataset)
    {
        try
        {
            // A durable delay races the export, and the execution records which completed first.
            return await Export(dataset).WaitAsync(TimeSpan.FromHours(1)).WithId("export");
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
    // </orleans0225_fix_timeout>

    private static async DurableTask<string> Export(string dataset) => $"exports/{dataset}";
}
