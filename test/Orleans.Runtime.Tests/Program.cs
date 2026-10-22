using Orleans.TestingHost;

namespace Tester
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || !int.TryParse(args[0], out _))
            {
                return await (args.Any(arg => arg is "-automated" or "@@")
                    ? Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args)
                    : Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, global::Orleans.Runtime.Tests.SelfRegisteredExtensions.AddSelfRegisteredExtensions));
            }

            await StandaloneSiloHost.Main(args);
            return 0;
        }
    }
}
