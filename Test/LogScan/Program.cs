using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LogScan
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Usage();
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "-noinvoke":
                    Console.WriteLine("Requests:");
                    WriteMissing(true, "EnqueueOutgoing", "InvokeIncoming", args.Skip(1));
                    Console.WriteLine("Responses:");
                    WriteMissing(true, "CreateResponse", "InvokeIncomingResponse", args.Skip(1));
                    break;
                case "-noresponse":
                    WriteMissing(false, "EnqueueOutgoing", "EnqueueOutgoingResponse|InvokeIncomingResponse", args.Skip(1));
                    break;
                default:
                    Usage();
                    break;
            }
        }

        static void Usage()
        {
            Console.WriteLine("Usage: LogScan [-noinvoke | -noresponse] logfiles");            
        }

        static void WriteLines(IEnumerable<object> lines)
        {
            foreach (var o in lines)
            {
                Console.WriteLine("{0}", o);
            }
        }

        static void WriteMissing(bool oneway, string starts, string ends, IEnumerable<string> filenames)
        {
            var tags = new HashSet<string>(starts.Split('|').Concat(ends.Split('|')));
            var lines = filenames
                .SelectMany(File.ReadAllLines)
                .SelectMany(MessageLine.TryParse);
            var found = lines
                .Where(m => oneway || m.Direction != "OneWay")
                .Where(m => tags.Contains(m.LifecycleTag))
                .ToDictionaryOfSets(m => m.LifecycleTag, m => m.Key);
            var missing = new HashSet<string>(found.GetAll(starts.Split('|')).Except(found.GetAll(ends.Split('|'))));
            WriteLines(lines.Where(m => missing.Contains(m.Key)));
        }

    }

    static class Util
    {
        public static IEnumerable<TV> GetAll<TK, TV>(this Dictionary<TK, HashSet<TV>> dict, IEnumerable<TK> keys)
        {
            HashSet<TV> values;
            var none = new HashSet<TV>();
            return keys.SelectMany(k => dict.TryGetValue(k, out values) ? values : none);
        }

        public static Dictionary<TK,HashSet<TV>> ToDictionaryOfSets<T,TK,TV>(this IEnumerable<T> input, Func<T,TK> key, Func<T,TV> value)
        {
            return input
                .Select(i => new KeyValuePair<TK, TV>(key(i), value(i)))
                .GroupBy(p => p.Key)
                .ToDictionary(g => g.Key, g => new HashSet<TV>(g.Select(p => p.Value)));
        }

        public static void WriteLines(this IEnumerable<string> lines)
        {
            foreach (var s in lines)
            {
                Console.WriteLine("{0}", s);
            }
        }
    }
    class MessageLine
    {
        public string LifecycleTag;
        public string Direction;
        public string Sender;
        public string Target;
        public string Id;
        //public string PriorId;
        public string DebugContext;
        public string Line;

        public string Key
        {
            get { return String.Format("{0}->{1}#{2}", Sender, Target, Id); }
        }

        public override string ToString()
        {
            return Line;
        }

        private static readonly Regex Pattern = new Regex(
            "VERBOSE\\s+Message.+Message\\s+(\\w+)\\s+(ReadOnly\\s+)?(NewPlacement\\s+)?(\\w+)\\s+(.*)->(.*)\\s+#(\\d+)(>\\d+)?:\\s+(.*)");

        public static IEnumerable<MessageLine> TryParse(string line)
        {
            var match = Pattern.Match(line);
            if (match.Success)
            {
                var message = new MessageLine
                {
                    Line = line,
                    LifecycleTag = match.Groups[1].Value,
                    Direction = match.Groups[4].Value,
                    Sender = match.Groups[5].Value,
                    Target = match.Groups[6].Value,
                    Id = match.Groups[7].Value,
                    DebugContext = match.Groups[9].Value
                };
                if (message.Direction == "Response")
                {
                    message.Sender = match.Groups[6].Value;
                    message.Target = match.Groups[5].Value;
                    if (! message.LifecycleTag.EndsWith("Response"))
                        message.LifecycleTag += "Response";
                }
                yield return message;
            }
            yield break;
        }
    }
}
