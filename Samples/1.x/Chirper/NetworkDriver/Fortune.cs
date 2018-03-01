using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace FortuneCookies
{
    public class Fortune
    {
        private List<string> fortunes;
        private Random rand;

        private const string defaultFileName = ".\\fortune.txt";

        public Fortune() : this(defaultFileName) {}

        public Fortune(string fileName)
        {
            using (var reader = File.OpenText(fileName))
            {
                Initialize(reader);
            }
        }

        public Fortune(TextReader input)
        {
            Initialize(input);
        }

        private void Initialize(TextReader input)
        {
            rand = new Random();
            fortunes = new List<string>();

            StringBuilder sb = new StringBuilder();
            string line = input.ReadLine();
            while (line != null)
            {
                if (line == "%")
                {
                    if (sb.Length > 0)
                    {
                        fortunes.Add(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }
                    sb.Append(line);
                }
                line = input.ReadLine();
            }
            if (sb.Length > 0)
            {
                fortunes.Add(sb.ToString());
            }
        }

        public string GetFortune()
        {
            var n = rand.Next(fortunes.Count);
            return fortunes[n];
        }
    }
}
