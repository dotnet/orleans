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

namespace Orleans.Runtime
{
    /// <summary>
    /// Interface of Orleans runtime for logging services. 
    /// </summary>
    [Serializable]
    public abstract class Logger
    {
        /// <summary> Severity levels for log messages. </summary>
        public enum Severity
        {
            Off = TraceLevel.Off,
            Error = TraceLevel.Error,
            Warning = TraceLevel.Warning,
            Info = TraceLevel.Info,
            Verbose = TraceLevel.Verbose,
            Verbose2 = TraceLevel.Verbose + 1,
            Verbose3 = TraceLevel.Verbose + 2
        }

        /// <summary> Current SeverityLevel set for this logger. </summary>
        public abstract Severity SeverityLevel
        {
            get;
        }

        /// <summary> Whether the current SeverityLevel would output <c>Warning</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsWarning
        {
            get { return SeverityLevel >= Severity.Warning; }
        }

        /// <summary> Whether the current SeverityLevel would output <c>Info</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsInfo
        {
            get { return SeverityLevel >= Severity.Info; }
        }

        /// <summary> Whether the current SeverityLevel would output <c>Verbose</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsVerbose
        {
            get { return SeverityLevel >= Severity.Verbose; }
        }

        /// <summary> Whether the current SeverityLevel would output <c>Verbose2</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsVerbose2
        {
            get { return SeverityLevel >= Severity.Verbose2; }
        }

        /// <summary> Whether the current SeverityLevel would output <c>Verbose3</c> messages for this logger. </summary>
        [DebuggerHidden]
        public bool IsVerbose3
        {
            get { return SeverityLevel >= Severity.Verbose3; }
        }

        /// <summary> Output the specified message at <c>Verbose</c> log level. </summary>
        public abstract void Verbose(string format, params object[] args);

        /// <summary> Output the specified message at <c>Verbose2</c> log level. </summary>
        public abstract void Verbose2(string format, params object[] args);

        /// <summary> Output the specified message at <c>Verbose3</c> log level. </summary>
        public abstract void Verbose3(string format, params object[] args);

        /// <summary> Output the specified message at <c>Info</c> log level. </summary>
        public abstract void Info(string format, params object[] args);

#region Public log methods using int LogCode categorization.
        /// <summary> Output the specified message and Exception at <c>Error</c> log level with the specified log id value. </summary>
        public abstract void Error(int logCode, string message, Exception exception = null);
        /// <summary> Output the specified message at <c>Warning</c> log level with the specified log id value. </summary>
        public abstract void Warn(int logCode, string format, params object[] args);
        /// <summary> Output the specified message and Exception at <c>Warning</c> log level with the specified log id value. </summary>
        public abstract void Warn(int logCode, string message, Exception exception);
        /// <summary> Output the specified message at <c>Info</c> log level with the specified log id value. </summary>
        public abstract void Info(int logCode, string format, params object[] args);
        /// <summary> Output the specified message at <c>Verbose</c> log level with the specified log id value. </summary>
        public abstract void Verbose(int logCode, string format, params object[] args);
        /// <summary> Output the specified message at <c>Verbose2</c> log level with the specified log id value. </summary>
        public abstract void Verbose2(int logCode, string format, params object[] args);
        /// <summary> Output the specified message at <c>Verbose3</c> log level with the specified log id value. </summary>
        public abstract void Verbose3(int logCode, string format, params object[] args);
#endregion
    }
}
