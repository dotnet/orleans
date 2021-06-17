using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace OneBoxDeployment.Utilities
{
    /// <summary>
    /// Platform specific utility functions.
    /// </summary>
    public static class PlatformUtilities
    {
        /// <summary>
        /// This is one of the known platforms. This is constant is used in platform specific operations,
        /// such as finding "Program Files" in Windows.
        /// </summary>
        private const string KnownPlatformWindows = "windows";


        /// <summary>
        /// Finds a free port from an ephemeral range.
        /// </summary>
        /// <returns>The found free port.</returns>
        public static int GetFreePortFromEphemeralRange()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            return port;
        }


        /// <summary>
        /// Gets <see cref="Process"/> handles with the given <paramref name="processNames"/> parameters.
        /// </summary>
        /// <param name="processNames">The names of the processes to search for.</param>
        /// <returns>The list of processes found with <paramref name="processNames"/> parameters.</returns>
        public static IList<Process> GetRunningProcessHandles(IEnumerable<string> processNames)
        {
            if(processNames == null)
            {
                throw new ArgumentNullException(nameof(processNames));
            }

            return processNames.Select(Process.GetProcessesByName).Where(processes => processes.Length > 0).SelectMany(processes => processes).ToList();
        }


        /// <summary>
        /// Gets <see cref="Process"/> handles with the given <paramref name="processNames"/> parameters.
        /// </summary>
        /// <param name="processNames">The names and filepaths of the processes to search for.</param>
        /// <returns>The list of processes found with <paramref name="processNames"/> parameters.</returns>
        public static IList<Process> GetRunningProcessHandles(IEnumerable<Tuple<string, string>> processNames)
        {
            if(processNames == null)
            {
                throw new ArgumentNullException(nameof(processNames));
            }

            return processNames.Select(i => Process.GetProcessesByName(i.Item1).Where(j => j.MainModule.FileName == i.Item2)).Where(procs => procs.Any()).SelectMany(procs => procs).ToList();
        }


        /// <summary>
        /// Calls a command line application with the given parameters.
        /// </summary>
        /// <param name="pathAndFileName">The application path and filename to call.</param>
        /// <param name="arguments">The arguments to give to the application.</param>
        /// <param name="timeoutInMilliseconds">The call timeout in milliseconds. Defaults to 1000 ms, or 10 s, currently.</param>
        /// <returns>The process exit code.</returns>
        public static ProcessHandle CallApplication(string pathAndFileName, string arguments, int timeoutInMilliseconds = 1000)
        {
            var processStartInfo = CreateProcessStartInfo(pathAndFileName, arguments);
            return StartProcessWithLogging(processStartInfo);
        }


        /// <summary>
        /// Starts a process with the given parameters.
        /// </summary>
        /// <param name="startInfo">The <see cref="ProcessStartInfo"/> to use to start the process.</param>
        /// <returns>The started process.</returns>
        public static Process StartProcess(ProcessStartInfo startInfo)
        {
            var p = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            p.Start();

            return p;
        }


        /// <summary>
        /// Kills the processes found with the given process names.
        /// </summary>
        /// <param name="processNames">The names of the processes to be killed.</param>
        public static void KillProcesses(IEnumerable<string> processNames)
        {
            if(processNames == null)
            {
                throw new ArgumentNullException(nameof(processNames));
            }

            foreach(var processToBeKilled in GetRunningProcessHandles(processNames))
            {
                processToBeKilled.Kill();
            }
        }


        /// <summary>
        /// Creates a <see cref="ProcessStartInfo"/> to start processes.
        /// </summary>
        /// <param name="pathAndFilename">The path and filename of the executable to load.</param>
        /// <param name="arguments">The arguments to be used.</param>
        /// <param name="encoding">The encoding used for <em>stdout</em> and <em>stderr</em>.</param>
        /// <param name="workingDirectory">The working directory of the executable.</param>
        /// <param name="environmentVariables">The environment variables to be used.</param>
        /// <returns>A pre-built <see cref="ProcessStartInfo"/></returns>.
        public static ProcessStartInfo CreateProcessStartInfo(string pathAndFilename, string arguments = null, Encoding encoding = null, string workingDirectory = null, IEnumerable<KeyValuePair<string, string>> environmentVariables = null)
        {
            if(pathAndFilename == null)
            {
                throw new ArgumentNullException(nameof(pathAndFilename));
            }

            //The commented lines here are present in .NET Full framework but not in .NET Core.
            var psi = new ProcessStartInfo
            {
                FileName = pathAndFilename,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                //WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                //ErrorDialog = false,
                Arguments = arguments,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if(workingDirectory != null)
            {
                psi.WorkingDirectory = workingDirectory;
            }

            if(encoding != null)
            {
                psi.StandardOutputEncoding = encoding;
                psi.StandardErrorEncoding = encoding;
            }

            if(environmentVariables != null)
            {
                foreach(var pair in environmentVariables)
                {
                    //psi.EnvironmentVariables[pair.Key] = pair.Value;
                    psi.Environment[pair.Key] = pair.Value;
                }
            }

            return psi;
        }


        /// <summary>
        /// Starts a process with the given parameters.
        /// </summary>
        /// <param name="startInfo">The <see cref="ProcessStartInfo"/> to use to start the process.</param>
        /// <returns>The started process with attached streams.</returns>
        public static ProcessHandle StartProcessWithLogging(ProcessStartInfo startInfo)
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var outputData = Observable.FromEventPattern<DataReceivedEventHandler, DataReceivedEventArgs>(h => process.OutputDataReceived += h, h => process.OutputDataReceived -= h).Select(ep => ep.EventArgs.Data).Publish().RefCount();
            var errorData = Observable.FromEventPattern<DataReceivedEventHandler, DataReceivedEventArgs>(h => process.ErrorDataReceived += h, h => process.ErrorDataReceived -= h).Select(ep => ep.EventArgs.Data).Publish().RefCount();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return new ProcessHandle(process, outputData, errorData);
        }


        /// <summary>
        /// Determines the Program Files base directory.
        /// </summary>
        /// <returns>The Program files base directory.</returns>
        /// <remarks>Only for <em>Windows</em></remarks>
        public static string GetDefaultSystemInstallationBasePath()
        {
            //Currently we support tests only on Windows.
            if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return RuntimeInformation.OSArchitecture == Architecture.X86 ? Environment.GetEnvironmentVariable("ProgramFiles(x86)") : Environment.GetEnvironmentVariable("ProgramFiles(x64)");
            }

            throw new InvalidOperationException($"Platform {RuntimeInformation.OSDescription} is not supported.");
        }
    }
}
