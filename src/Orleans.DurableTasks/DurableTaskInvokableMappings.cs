using System.Distributed.DurableTasks;
using Orleans.DurableTasks;
using Orleans.Runtime;

[assembly: InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[assembly: InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]
