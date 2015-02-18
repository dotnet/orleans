using System;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace Orleans.Test.BabySitter
{
    public class BabySitter
    {
        private const string ErrorReportFileName = "BabysitterError.txt";

        static void Main(string[] args)
        {
            try
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: BabySitter <outputfilename> <programName> <...parameters to program>");
                }
                else
                {
                    string outputFileName = args[0];
                    string image = args[1];
                    StringBuilder parameters = new StringBuilder();
                    for (int i = 2; i < args.Length; i++)
                    {
                        if (args[i].StartsWith("\"") && args[i].EndsWith("\""))
                            parameters.AppendFormat(" {0} ", args[i]);
                        else
                            parameters.AppendFormat(" \"{0}\" ", args[i]);
                    }

                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = image;
                    startInfo.CreateNoWindow = true;
                    startInfo.WorkingDirectory = Path.GetDirectoryName(image);
                    startInfo.UseShellExecute = false;
                    startInfo.Arguments = parameters.ToString();
                    startInfo.RedirectStandardOutput = true;
                    startInfo.RedirectStandardError = true;
                    Process process = Process.Start(startInfo);
                    bool alreadyWroteBabysitterErrorPreemble = false;
                    do
                    {
                        try
                        {
                            string line;
                            while (null != (line = process.StandardOutput.ReadLine()))
                            {
                                using (StreamWriter sw = new StreamWriter(outputFileName, true))
                                {
                                    sw.WriteLine(line);
                                    sw.Flush();
                                }
                            }
                            while (null != (line = process.StandardError.ReadLine()))
                            {
                                using (StreamWriter sw = new StreamWriter(ErrorReportFileName, true))
                                {
                                    if(!alreadyWroteBabysitterErrorPreemble)
                                    {
                                        sw.WriteLine(parameters.ToString());
                                        alreadyWroteBabysitterErrorPreemble = true;
                                    }
                                    sw.WriteLine(line);
                                    sw.Flush();
                                }
                            }
                        }
                        catch (Exception exc)
                        {
                            Console.WriteLine(exc.ToString());
                        }
                    } while (!process.HasExited);
                }
            }
            catch (Exception ex)
            {
                Exception temp = ex;
                using (StreamWriter sw = new StreamWriter(ErrorReportFileName, true))
                {
                    sw.WriteLine("Startup error...");
                    while (null != temp)
                    {
                        sw.WriteLine(temp);
                        if (temp is AggregateException)
                        {
                            foreach (var iex in ((AggregateException)temp).InnerExceptions)
                            {
                                sw.WriteLine(iex);
                                sw.Flush();
                            }
                        }
                        temp = temp.InnerException;
                    }
                    sw.Flush();
                }
            }
        }
    }
}
