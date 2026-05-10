using System.Runtime.CompilerServices;
using EmptyFiles;

namespace Orleans.Journaling.Json.Tests;

internal static class VerifyModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        // Verify defaults to treating unknown extensions as binary (and refuses string overloads with
        // a binary target). Snapshot files in this project use the .jsonl extension purely so editors
        // syntax-highlight them as JSON Lines; their contents are always UTF-8 text. The
        // <see cref="FileExtensions"/> registry lives in the <c>EmptyFiles</c> package brought in
        // transitively by Verify.
        FileExtensions.AddTextExtension("jsonl");
    }
}
