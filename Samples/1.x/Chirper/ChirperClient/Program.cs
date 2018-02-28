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
