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

// Unset this to run external local silo
// http://dotnet.github.io/orleans/Step-by-step-Tutorials/Running-in-a-Stand-alone-Silo
#define USE_INPROC_SILO

using System;
using Orleans;
using HelloWorldInterfaces;

namespace HelloWorld
{
    /// <summary>
    /// Orleans test silo host
    /// </summary>
    public class Program
    {
        static void Main(string[] args)
        {
#if USE_INPROC_SILO
            // The Orleans silo environment is initialized in its own app domain in order to more
            // closely emulate the distributed situation, when the client and the server cannot
            // pass data via shared memory.
            AppDomain hostDomain = AppDomain.CreateDomain("OrleansHost", null, new AppDomainSetup
            {
                AppDomainInitializer = InitSilo,
                AppDomainInitializerArguments = args,
            });
#endif
            GrainClient.Initialize("DevTestClientConfiguration.xml");

            var friend = GrainFactory.GetGrain<IHello>(0);
            Console.WriteLine("\n\n{0}\n\n", friend.SayHello("Good morning!").Result);

            Console.WriteLine("Orleans Silo is running.\nPress Enter to terminate...");
            Console.ReadLine();

#if USE_INPROC_SILO
            hostDomain.DoCallBack(ShutdownSilo);
#endif
        }

#if USE_INPROC_SILO
        static void InitSilo(string[] args)
        {
            hostWrapper = new OrleansHostWrapper(args);

            if (!hostWrapper.Run())
            {
                Console.Error.WriteLine("Failed to initialize Orleans silo");
            }
        }

        static void ShutdownSilo()
        {
            if (hostWrapper != null)
            {
                hostWrapper.Dispose();
                GC.SuppressFinalize(hostWrapper);
            }
        }

        private static OrleansHostWrapper hostWrapper;
#endif
    }
}
