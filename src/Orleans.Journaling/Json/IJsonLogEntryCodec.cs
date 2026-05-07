namespace Orleans.Journaling.Json;

internal interface IJsonLogEntryCodec
{
    void Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine);
}
