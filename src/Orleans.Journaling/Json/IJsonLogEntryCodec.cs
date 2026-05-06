using System.Text.Json;

namespace Orleans.Journaling.Json;

internal interface IJsonLogEntryCodec
{
    void Apply(JsonElement entry, IDurableStateMachine stateMachine);
}
