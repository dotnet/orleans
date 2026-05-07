namespace Orleans.Journaling.Json;

internal interface IJsonLogEntryCodec
{
    void Apply(JsonOperationEntry entry, IDurableStateMachine stateMachine);
}
