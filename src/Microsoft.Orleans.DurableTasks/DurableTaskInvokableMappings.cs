using Orleans.DurableTasks.Protocol;
using Orleans;
using Orleans.DurableTasks;
using Orleans.Runtime;

[assembly: InvokableBaseType(typeof(GrainReference), typeof(DurableTask), typeof(DurableTaskRequest))]
[assembly: InvokableBaseType(typeof(GrainReference), typeof(DurableTask<>), typeof(DurableTaskRequest<>))]

// The abstractions declare their persisted types' aliases; this assembly generates their type metadata.
[assembly: GenerateCodeForDeclaringAssembly(typeof(DurableTaskResponse))]