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
using System.Reflection;

using Orleans.Runtime;

namespace Orleans.Counter.Control
{
    class Program
    {
        static int Main(string[] args)
        {
            var prog = new CounterControl();

            // Program ident
            AssemblyName thisProgram = Assembly.GetExecutingAssembly().GetName();
            var progTitle = string.Format("{0} v{1}", thisProgram.Name, thisProgram.Version.ToString());
            ConsoleText.WriteStatus(progTitle);
            Console.Title = progTitle;

            int result;
            if (!prog.ParseArguments(args))
            {
                prog.PrintUsage();
                result = -1;
            }
            else
            {
                result = prog.Run();
            }

            if (prog.PauseAtEnd)
            {
                Console.WriteLine("Press any key to exit");
                Console.ReadKey();
            }

            return result;
        }
    }
}