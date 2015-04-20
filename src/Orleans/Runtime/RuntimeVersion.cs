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

﻿using System;
using System.Diagnostics;
using System.Reflection;


namespace Orleans.Runtime
{
    internal class RuntimeVersion
    {
        /// <summary>
        /// The full version string of the Orleans runtime, eg: '2012.5.9.51607 Build:12345 Timestamp: 20120509-185359'
        /// </summary>
        public static string Current
        {
            get
            {
                Assembly thisProg = Assembly.GetExecutingAssembly();
                FileVersionInfo progVersionInfo = FileVersionInfo.GetVersionInfo(thisProg.Location);
                bool isDebug = IsAssemblyDebugBuild(thisProg);
                string productVersion = progVersionInfo.ProductVersion + (isDebug ? " (Debug)." : " (Release)."); // progVersionInfo.IsDebug; does not work
                return string.IsNullOrEmpty(productVersion) ? ApiVersion : productVersion;
            }
        }

        /// <summary>
        /// The ApiVersion of the Orleans runtime, eg: '1.0.0.0'
        /// </summary>
        public static string ApiVersion
        {
            get
            {
                AssemblyName libraryInfo = Assembly.GetExecutingAssembly().GetName();
                return libraryInfo.Version.ToString();
            }
        }

        /// <summary>
        /// The FileVersion of the Orleans runtime, eg: '2012.5.9.51607'
        /// </summary>
        public static string FileVersion
        {
            get
            {
                Assembly thisProg = Assembly.GetExecutingAssembly();
                FileVersionInfo progVersionInfo = FileVersionInfo.GetVersionInfo(thisProg.Location);
                string fileVersion = progVersionInfo.FileVersion;
                return string.IsNullOrEmpty(fileVersion) ? ApiVersion : fileVersion;

            }
        }

        /// <summary>
        /// The program name string for the Orleans runtime, eg: 'OrleansHost'
        /// </summary>
        public static string ProgramName
        {
            get
            {
                Assembly thisProg = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                AssemblyName progInfo = thisProg.GetName();
                return progInfo.Name;
            }
        }

        /// <summary>
        /// Writes the Orleans program ident info to the Console, eg: 'OrleansHost v2012.5.9.51607 Build:12345 Timestamp: 20120509-185359'
        /// </summary>
        public static void ProgamIdent()
        {
            string progTitle = string.Format("{0} v{1}", ProgramName, Current);
            ConsoleText.WriteStatus(progTitle);
#if DEBUG
            Console.Title = progTitle;
#endif
        }

        private static bool IsAssemblyDebugBuild(Assembly assembly)
        {
            foreach (var attribute in assembly.GetCustomAttributes(false))
            {
                var debuggableAttribute = attribute as DebuggableAttribute;
                if (debuggableAttribute != null)
                    return debuggableAttribute.IsJITTrackingEnabled;
            }
            return false;
        }

    }
}