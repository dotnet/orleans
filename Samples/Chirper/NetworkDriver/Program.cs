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
using System.Reflection;
using Orleans;

namespace Orleans.Samples.Chirper.Network.Driver
{
    class Program
    {
        static void Main(string[] args)
        {
            var prog = new ChirperNetworkDriver();
            int exitCode = 0;

            try
            {
                // Program ident
                AssemblyName thisProg = Assembly.GetExecutingAssembly().GetName();
                string progTitle = string.Format("{0} v{1}",
                    thisProg.Name,
                    thisProg.Version);
                Console.WriteLine(progTitle);
                Console.Title = progTitle;


                try
                {
                    if (!prog.ParseArguments(args))
                    {
                        prog.PrintUsage();
                        exitCode = -1;
                    }
                    else
                    {
                        exitCode = prog.Run();
                    }
                }
                catch (Exception exc)
                {
                    Console.WriteLine("{0} halting due to error - {1}. Exception:{2}", thisProg.Name, exc.Message, exc);
                    exitCode = 1;
                }

                Console.WriteLine("==> Press any key to exit <==");
                Console.ReadKey();

                try
                {
                    prog.Stop();
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Ignoring shutdown error: " + exc);
                }
            }
            finally
            {
                prog.Dispose();
                Environment.Exit(exitCode);
            }
        }
    }
}
