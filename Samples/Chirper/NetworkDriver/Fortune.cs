/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
