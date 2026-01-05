using System.Diagnostics;

namespace Orleans.Runtime;

public static class ActivitySources
{
    public static string ApplicationGrainActivitySourceName = "Microsoft.Orleans.Application";
    public static string RuntimeActivitySourceName = "Microsoft.Orleans.Runtime";

    internal static readonly ActivitySource ApplicationGrainSource = new(ApplicationGrainActivitySourceName, "1.0.0");
    internal static readonly ActivitySource RuntimeGrainSource = new(RuntimeActivitySourceName, "1.0.0");

}
