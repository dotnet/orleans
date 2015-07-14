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

using Orleans;
using System;
using System.IO;

namespace AdventureSetup
{
    class Program
    {
        static int Main(string [] args)
        {
            string mapFileName = "..\\..\\AdventureMap.json";

            switch (args.Length)
            {
                default:
                    Console.WriteLine("*** Invalid command line arguments.");
                    return -1;
                case 0:
                    break;
                case 1:
                    mapFileName = args[0];
                    break;
            }

            if (!File.Exists(mapFileName))
            {
                Console.WriteLine("*** File not found: {0}", mapFileName);
                return -2;
            }

            GrainClient.Initialize();

            Console.WriteLine("Map file name is '{0}'.", mapFileName);
            Console.WriteLine("Setting up Adventure, please wait ...");
            Adventure adventure = new Adventure();     
            adventure.Configure(mapFileName).Wait();
            Console.WriteLine("Adventure setup completed.");
            Console.ReadLine();

            return 0;
        }
    }
}
