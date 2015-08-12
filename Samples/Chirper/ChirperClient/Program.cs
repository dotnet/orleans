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
using Orleans;
using System.Threading.Tasks;

namespace Orleans.Samples.Chirper.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            ChirperClient client = new ChirperClient();
            bool ok = client.ParseArgs(args);

            if (ok)
            {
                ok = false;

                try
                {
                    Task<bool> run = client.Run();
                    if (client.IsPublisher)
                    {
                        Console.Write("Enter a comment: ");
                        string line = Console.ReadLine();
                        while ( line != null && line.ToLower().Trim() != "quit")
                        {
                            client.PublishMessage(line).Wait();
                            Console.Write("Enter a comment: ");
                            line = Console.ReadLine();
                        }

                        Environment.Exit(0);
                    }
                    run.Wait();
                    if (run.IsFaulted)
                    {
                        Console.WriteLine("Error running client program: " + run.Exception);
                    }
                    else
                    {
                        ok = run.Result;
                    }
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Error running client program: " + exc);
                }
            }
            else
            {
                client.PrintUsage();
            }
            int rc = ok ? 0 : 1;
            Environment.Exit(rc);
        }
    }
}
