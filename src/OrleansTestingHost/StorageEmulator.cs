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
using System.Diagnostics;
using System.IO;
using System.Linq;


namespace Orleans.TestingHost
{
    /// <summary>
    /// A wrapper on Azure Storage Emulator.
    /// </summary>
    /// <remarks>It might be tricky to implement this as a <see cref="IDisposable">IDisposable</see>, isolated, autonomous instance, 
    /// see at <see href="http://azure.microsoft.com/en-us/documentation/articles/storage-use-emulator/">Use the Azure Storage Emulator for Development and Testing</see>
    /// for pointers.</remarks>
    public static class StorageEmulator
    {
        /// <summary>
        /// The storage emulator process name. One way to enumerate running process names is
        /// Get-Process | Format-Table Id, ProcessName -autosize. If there were multiple storage emulator
        /// processes running, they would named WASTOR~1, WASTOR~2, ... WASTOR~n.
        /// </summary>
        private const string storageEmulatorProcessName = "WAStorageEmulator";


        /// <summary>
        /// Is the storage emulator already started.
        /// </summary>
        /// <returns></returns>
        public static bool IsStarted()
        {
            return GetStorageEmulatorProcess() != null;
        }


        /// <summary>
        /// Checks if the storage emulator exists, i.e. is installed.
        /// </summary>
        public static bool Exists
        {
            get
            {
                return File.Exists(GetStorageEmulatorPath());
            }
        }


        /// <summary>
        /// Storage Emulator help.
        /// </summary>
        /// <returns>Storage emulator help.</returns>
        public static string Help()
        {
            if (!IsStarted()) return "Error happened. Has StorageEmulator.Start() been called?";

            try
            {
                //This process handle returns immediately.
                using(var process = Process.Start(CreateProcessArguments("help")))
                {
                    process.WaitForExit();
                    var help = string.Empty;
                    while(!process.StandardOutput.EndOfStream)
                    {
                        help += process.StandardOutput.ReadLine();
                    }

                    return help;
                }
            }
            catch (Exception exc)
            {
                return exc.ToString();
            }
        }


        /// <summary>
        /// Tries to start the storage emulator.
        /// </summary>
        /// <returns><em>TRUE</em> if the process was started sucessfully. <em>FALSE</em> otherwise.</returns>
        public static bool TryStart()
        {
            if (!StorageEmulator.Exists)
                return false;

            return Start();
        }


        /// <summary>
        /// Starts the storage emulator if not already started.
        /// </summary>
        /// <returns><em>TRUE</em> if the process was stopped succesfully or was already started. <em>FALSE</em> otherwise.</returns>
        public static bool Start()
        {
            if (IsStarted()) return true;

            try
            {
                //This process handle returns immediately.
                using(var process = Process.Start(CreateProcessArguments("start")))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        /// Stops the storage emulator if started.
        /// </summary>
        /// <returns><em>TRUE</em> if the process was stopped succesfully or was already stopped. <em>FALSE</em> otherwise.</returns>
        public static bool Stop()
        {
            if (!IsStarted()) return false;

            try
            {
                //This process handle returns immediately.
                using(var process = Process.Start(CreateProcessArguments("stop")))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        /// Creates a new <see cref="ProcessStartInfo">ProcessStartInfo</see> to be used as an argument
        /// to other operations in this class.
        /// </summary>
        /// <param name="arguments">The arguments.</param>
        /// <returns>A new <see cref="ProcessStartInfo">ProcessStartInfo</see> that has the given arguments.</returns>
        private static ProcessStartInfo CreateProcessArguments(string arguments)
        {
            return new ProcessStartInfo(GetStorageEmulatorPath())
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = true,
                LoadUserProfile = true,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = arguments
            };
        }


        /// <summary>
        /// Queries the storage emulator process from the system.
        /// </summary>
        /// <returns></returns>
        private static Process GetStorageEmulatorProcess()
        {
            return Process.GetProcessesByName(storageEmulatorProcessName).FirstOrDefault();
        }
        
                
        /// <summary>
        /// Returns a full path to the storage emulator executable, including the executable name and file extension.
        /// </summary>
        /// <returns>A full path to the storage emulator executable.</returns>
        private static string GetStorageEmulatorPath()
        {
            return Path.Combine(GetProgramFilesBasePath(), @"Microsoft SDKs\Azure\Storage Emulator\WAStorageEmulator.exe");
        }


        /// <summary>
        /// Determines the Program Files base directory.
        /// </summary>
        /// <returns>The Program files base directory.</returns>
        private static string GetProgramFilesBasePath()
        {
            return Environment.Is64BitOperatingSystem ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }
    }
}
