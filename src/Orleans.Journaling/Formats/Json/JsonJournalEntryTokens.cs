namespace Orleans.Journaling.Json;

internal static class JsonJournalEntryFields
{
    public const string Index = "index";
    public const string Item = "item";
    public const string Items = "items";
    public const string Key = "key";
    public const string Message = "message";
    public const string State = "state";
    public const string Value = "value";
    public const string Version = "version";
}

internal static class JsonJournalEntryCommands
{
    public const string Add = "add";
    public const string Canceled = "canceled";
    public const string Clear = "clear";
    public const string Completed = "completed";
    public const string Dequeue = "dequeue";
    public const string Enqueue = "enqueue";
    public const string Faulted = "faulted";
    public const string Insert = "insert";
    public const string Pending = "pending";
    public const string Remove = "remove";
    public const string RemoveAt = "removeAt";
    public const string Set = "set";
    public const string Snapshot = "snapshot";
}
