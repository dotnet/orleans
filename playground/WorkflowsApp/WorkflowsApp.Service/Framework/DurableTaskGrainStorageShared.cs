using System.Distributed.DurableTasks;
using Orleans.DurableTasks;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace WorkflowsApp.Service;

internal sealed class DurableTaskGrainStorageShared(
    IGrainFactory grainFactory,
    IFieldCodec<TaskId> taskIdCodec,
    IFieldCodec<DurableTaskState> taskStateCodec,
    IFieldCodec<DurableTaskResponse> responseCodec,
    IFieldCodec<IDurableTaskObserver> observerCodec,
    IFieldCodec<DateTimeOffset> dateTimeOffsetCodec,
    IFieldCodec<IDurableTaskRequest> requestCodec,
    SerializerSessionPool serializerSessionPool,
    TimeProvider timeProvider)
{
    public readonly IGrainFactory GrainFactory = grainFactory;
    public readonly TimeProvider TimeProvider = timeProvider;
    public readonly IFieldCodec<TaskId> KeyCodec = taskIdCodec;
    public readonly IFieldCodec<DurableTaskState> ValueCodec = taskStateCodec;
    public readonly IFieldCodec<DurableTaskResponse> ResponseCodec = responseCodec;
    public readonly IFieldCodec<IDurableTaskObserver> ObserverCodec = observerCodec;
    public readonly IFieldCodec<DateTimeOffset> DateTimeOffsetCodec = dateTimeOffsetCodec;
    public readonly IFieldCodec<IDurableTaskRequest> RequestCodec = requestCodec;
    public readonly SerializerSessionPool SerializerSessionPool = serializerSessionPool;
}
