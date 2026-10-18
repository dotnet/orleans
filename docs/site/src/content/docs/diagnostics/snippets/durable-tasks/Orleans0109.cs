namespace Documentation.Diagnostics.DurableTasks;

public sealed class DocumentGrain : DurableGrain
{
    private readonly IDocumentHasher _hasher;
    private readonly IDurableValue<string?> _hash;

    public DocumentGrain(IDocumentHasher hasher)
    {
        _hasher = hasher;
        _hash = StateManager.GetOrAddValue<string?>("hash");
    }

#pragma warning disable ORLEANS0109
    // <orleans0109_violation>
    public async DurableTask<string> SealAsync(byte[] document)
    {
        // The hash runs on the thread pool, and the code after this await continues there, off the activation.
        var hash = await Task.Run(() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(document)));
        await Archive(hash).WithId("archive");
        return hash;
    }
    // </orleans0109_violation>
#pragma warning restore ORLEANS0109

    // <orleans0109_fix_inline>
    public async DurableTask<string> SealInlineAsync(byte[] document)
    {
        // The effect records the hash, and runs on the activation's scheduler.
        var hash = DurableTask.RunInline("hash", () => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(document)));
        await Archive(hash).WithId("archive");
        return hash;
    }
    // </orleans0109_fix_inline>

    // <orleans0109_fix_service>
    public async DurableTask<string> SealWithServiceAsync(byte[] document)
    {
        // Heavy work runs in a service, such as a stateless worker grain, which the step awaits.
        var hash = await DurableTask.Run(ct => _hasher.HashAsync(document, ct)).WithId("hash");
        await Archive(hash).WithId("archive");
        return hash;
    }
    // </orleans0109_fix_service>

    private async DurableTask<string> Archive(string hash) => _hash.Value = hash;
}
