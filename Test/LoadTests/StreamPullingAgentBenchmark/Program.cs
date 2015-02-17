using System.Linq;
using StreamPullingAgentBenchmark.EmbeddedSiloLoadTest;

namespace StreamPullingAgentBenchmark
{
    public class Program
    {
        public static int Main(string[] args)
        {
            // The command line parser doesn't recognize single-dash long options for longer than 1 char options, so add an extra "-".
            args = args.Select(s => s.Length > 2 && s[0] == '-' && s[1] != '-' ? "-" + s : s).ToArray();

            BaseOptions options;
            if (!Utilities.ParseArguments(args, out options))
            {
                return 1;
            }

            int result;
            switch (options.TestName.ToLowerInvariant())
            {
                case "streampullingagentbenchmark":
                {
                    var test = new StreamPullingAgentBenchmark.StreamPullingAgentBenchmark();
                    result = test.RunAsync(args).Result;
                    break;
                }
                case "newreminderloadtest":
                {
                    var test = new NewReminderLoadTest.NewReminderLoadTest();
                    result = test.RunAsync(args).Result;
                    break;
                }
                default:
                {
                    Utilities.LogAlways("Unknown test name");
                    result = 1;
                    break;
                }
            }

            return result;
        }
    }
}
