using Orleans;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Runtime;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableTasks;

// <durable_tasks_identity_contracts>
[GenerateSerializer, Immutable]
public sealed record PublishReceipt(TaskId Workflow, TaskId Archive, TaskId Copy, TaskId ArchiveOperation);

public interface IReportGrain : IGrainWithStringKey
{
    DurableTask<PublishReceipt> PublishAsync(string[] sections);

    Task<DurableSignalResult> ApproveAsync(bool approved);
}

public interface IRendererGrain : IGrainWithStringKey
{
    DurableTask<int> RenderAsync(string section);
}

public interface IArchiveGrain : IGrainWithStringKey
{
    DurableTask<bool> ArchiveAsync(string reportId, int version);
}
// </durable_tasks_identity_contracts>

// <durable_tasks_identity_tree>
public sealed class ReportGrain(IDurableTaskManager tasks) : DurableGrain, IReportGrain
{
    public static readonly DurableSignal<bool> Approved = new("approved");

    private string ReportId => this.GetPrimaryKeyString();

    public static string PublishTaskId(string reportId) => $"publish-{reportId}";

    // The caller starts this task under the root ID "publish-{reportId}", which DurableTask.Current.Id reports.
    public async DurableTask<PublishReceipt> PublishAsync(string[] sections)
    {
        // publish-{reportId}/$once:version, a named recorded effect.
        var version = DurableTask.Once("version", () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // publish-{reportId}/$child-1, the execution's first unnamed operation.
        var draftId = DurableTask.Once(Guid.NewGuid);

        // publish-{reportId}/render/0, render/1, and so on: a named combinator's inputs, in input order. Each renderer
        // stores its call under the caller's scope, $caller/{reportGrainId}/publish-{reportId}/render/{i}.
        await DurableTask.WhenAll(sections.Select(section => Renderer(section).RenderAsync(section))).WithId("render");

        // publish-{reportId}/check, then check$2, check$3, and so on: one name which this run repeats.
        foreach (var section in sections)
        {
            await Check(section, draftId).WithId("check");
        }

        // publish-{reportId}/$signal:approved, the signal's record beneath the invoked task.
        if (!await Approved.WaitAsync())
        {
            return new(DurableTask.Current.Id, default, default, default);
        }

        // A named detached task: the root "archive-{reportId}" on the archive grain. The execution records the start as
        // publish-{reportId}/$detach:{archiveGrainId}:archive-{reportId}, which the handle's Id reports.
        var archive = await Archive().ArchiveAsync(ReportId, (int)version).Detached().WithId($"archive-{ReportId}")
            .ScheduleAsync();

        // An unnamed detached task: its root ID derives from this grain, this task, and the next unnamed operation,
        // "{reportGrainId}/publish-{reportId}:$child-2".
        var copy = Archive().ArchiveAsync($"{ReportId}-copy", (int)version).Detached().Post();

        return new(DurableTask.Current.Id, archive.TargetTaskId, copy.TargetTaskId, archive.Id);
    }

    public async Task<DurableSignalResult> ApproveAsync(bool approved) =>
        await tasks.TrySignalAsync(PublishTaskId(ReportId), Approved, approved);

    private static async DurableTask<bool> Check(string section, Guid draftId) => section.Length > 0 && draftId != Guid.Empty;

    private IRendererGrain Renderer(string section) => GrainFactory.GetGrain<IRendererGrain>(section);

    private IArchiveGrain Archive() => GrainFactory.GetGrain<IArchiveGrain>("archive");
}
// </durable_tasks_identity_tree>

internal static class ReportClient
{
    // <durable_tasks_identity_client>
    public static async Task<PublishReceipt> PublishAsync(IClusterClient client, string reportId, string[] sections)
    {
        // The root ID comes from the report's identity, so a retried request reaches the same task.
        ScheduledTask<PublishReceipt> publish = await client.GetGrain<IReportGrain>(reportId)
            .PublishAsync(sections)
            .WithId(ReportGrain.PublishTaskId(reportId))
            .ScheduleAsync();

        // Outside a durable task, a handle's Id and TargetTaskId are both the root ID.
        Console.WriteLine($"{publish.Id} {publish.TargetTaskId}");
        return await publish;
    }
    // </durable_tasks_identity_client>

    // <durable_tasks_identity_lookup>
    public static async Task<DurableTaskStatus> GetArchiveStatusAsync(IClusterClient client, string reportId)
    {
        // A named root is addressed by its grain and its name, which every party derives from the report's identity.
        var archive = client.GetGrain<IArchiveGrain>("archive").GetDurableTask<bool>($"archive-{reportId}");
        return await archive.GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.Zero });
    }
    // </durable_tasks_identity_lookup>
}
