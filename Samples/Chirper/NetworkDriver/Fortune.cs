//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

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
