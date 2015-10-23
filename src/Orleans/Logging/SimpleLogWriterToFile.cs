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
using System.Net;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// Just a simple log writer wrapper class with public WriteToLog method directly, without formatting.
    /// Mainly to be used from tests and external utilities.
    /// </summary>
    public class SimpleLogWriterToFile : LogWriterToFile
    {
        /// <summary>
        /// Constructor, specifying the file to send output to.
        /// </summary>
        /// <param name="logFile">The log file to be written to.</param>
        public SimpleLogWriterToFile(FileInfo logFile)
            : base(logFile)
        {
        }

        /// <summary>
        /// Output message directly to log file -- no formatting is performed.
        /// </summary>
        /// <param name="msg">Message text to be logged.</param>
        /// <param name="severity">Severity of this log message -- ignored.</param>
        public void WriteToLog(string msg, Logger.Severity severity)
        {
            WriteLogMessage(msg, severity);
        }
    }
}