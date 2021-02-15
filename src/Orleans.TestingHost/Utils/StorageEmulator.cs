using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Orleans.TestingHost.Utils
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
        private static readonly string[] storageEmulatorProcessNames = new[]
        {
            "AzureStorageEmulator", // newest
            "Windows Azure Storage Emulator Service", // >= 2.7
            "WAStorageEmulator", // < 2.7
        };

        //The file names aren't the same as process names.
        private static readonly string[] storageEmulatorFilenames = new[]
        {
            "AzureStorageEmulator.exe", // >= 2.7
            "WAStorageEmulator.exe", // < 2.7
        };
        
        /// <summary>
        /// Is the storage emulator already started.
        /// </summary>
        /// <returns></returns>
        public static bool IsStarted()
        {
            return GetStorageEmulatorProcess();
        }


        /// <summary>
        /// Checks if the storage emulator exists, i.e. is installed.
        /// </summary>
        public static bool Exists
        {
            get
            {
                return GetStorageEmulatorPath() != null;
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
        /// <returns><em>TRUE</em> if the process was started successfully. <em>FALSE</em> otherwise.</returns>
        public static bool TryStart()
        {
            if (!StorageEmulator.Exists)
                return false;

            return Start();
        }

        /// <summary>
        /// Starts the storage emulator if not already started.
        /// </summary>
        /// <returns><em>TRUE</em> if the process was stopped successfully or was already started. <em>FALSE</em> otherwise.</returns>
        public static bool Start()
        {
            if (IsStarted()) return true;

            try
            {
                //This process handle returns immediately.
                using(var process = Process.Start(CreateProcessArguments("start")))
                {
                    if (process == null) return false;
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
        /// <returns><em>TRUE</em> if the process was stopped successfully or was already stopped. <em>FALSE</em> otherwise.</returns>
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
#pragma warning disable CA1416 // Validate platform compatibility
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
#pragma warning restore CA1416
        }

        /// <summary>
        /// Queries the storage emulator process from the system.
        /// </summary>
        /// <returns></returns>
        private static bool GetStorageEmulatorProcess()
        {
            foreach (var name in storageEmulatorProcessNames)
            {
                var ps = Process.GetProcessesByName(name);
                if (ps.Length != 0)
                {
                    foreach (var p in ps)
                        p.Dispose();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns a full path to the storage emulator executable, including the executable name and file extension.
        /// </summary>
        /// <returns>A full path to the storage emulator executable, or null if not found.</returns>
        private static string GetStorageEmulatorPath()
        {
            //Try to take the newest known emulator path. If it does not exist, try an older one.
            string exeBasePath = Path.Combine(GetProgramFilesBasePath(), @"Microsoft SDKs\Azure\Storage Emulator\");

            return storageEmulatorFilenames
                .Select(filename => Path.Combine(exeBasePath, filename))
                .FirstOrDefault(File.Exists);
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
