namespace Orleans.Journaling.Json;

internal interface IJsonJournalEntryCodec
{
    void Apply(ref JsonOperationReader reader, IJournaledState state);
}
