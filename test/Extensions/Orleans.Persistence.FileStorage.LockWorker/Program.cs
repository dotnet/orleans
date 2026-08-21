var lockPath = args[0];

using var stream = new FileStream(
    lockPath,
    FileMode.OpenOrCreate,
    FileAccess.ReadWrite,
    FileShare.None);

Console.WriteLine("READY");
await Console.Out.FlushAsync();
_ = await Console.In.ReadLineAsync();
