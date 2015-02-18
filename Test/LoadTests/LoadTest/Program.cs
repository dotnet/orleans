using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoadTest
{
    class Program
    {
        static void Main(string[] args)
        {
            LoadTest tester = new LoadTest();

            tester.Prologue();
            tester.Soramichi_LoadTest(1, 10, 8, 2000000);

            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
        }
    }
}
