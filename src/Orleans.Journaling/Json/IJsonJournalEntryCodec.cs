namespace Orleans.Journaling.Json;

internal interface IJsonJournalEntryCodec
{
    void Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine);
}
