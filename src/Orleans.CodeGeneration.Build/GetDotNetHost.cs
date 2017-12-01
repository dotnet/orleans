using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Orleans.CodeGeneration
{
    public class GetDotNetHost : MSBuildTask
    {
        [Output]
        public string DotNetHost { get; set; }
        
        public override bool Execute()
        {
            this.DotNetHost = TryFindDotNetExePath();
            return true;
        }

        private static string TryFindDotNetExePath()
        {
            var fileName = "dotnet";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                fileName += ".exe";
            }

            var mainModule = Process.GetCurrentProcess().MainModule;
            if (mainModule != null && !string.IsNullOrEmpty(mainModule.FileName)
                && Path.GetFileName(mainModule.FileName).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return mainModule.FileName;
            }

            // if Process.MainModule is not available or it does not equal "dotnet(.exe)?", fallback to navigating to the muxer
            // by using the location of the shared framework

            var fxDepsFile = AppDomain.CurrentDomain.GetData("FX_DEPS_FILE") as string;

            if (string.IsNullOrEmpty(fxDepsFile))
            {
                return fileName;
            }

            var muxerDir = new FileInfo(fxDepsFile) // Microsoft.NETCore.App.deps.json
                .Directory? // (version)
                .Parent? // Microsoft.NETCore.App
                .Parent? // shared
                .Parent; // DOTNET_HOME

            if (muxerDir == null)
            {
                return fileName;
            }

            var muxer = Path.Combine(muxerDir.FullName, fileName);
            return File.Exists(muxer)
                ? muxer
                : fileName;
        }
    }
}