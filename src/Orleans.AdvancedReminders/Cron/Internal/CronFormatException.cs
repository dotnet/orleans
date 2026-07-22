// The MIT License (MIT)
//
// Copyright (c) 2017 Hangfire OÜ
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// Derived from Cronos: https://github.com/HangfireIO/Cronos
#nullable enable
using System;
#if !NETSTANDARD1_0
using System.Runtime.Serialization;
#endif

namespace Orleans.AdvancedReminders.Cron.Internal
{
    /// <summary>
    /// Represents an exception that's thrown, when invalid Cron expression is given.
    /// </summary>
#if !NETSTANDARD1_0
    [Serializable]
#endif
    internal class CronFormatException : FormatException
    {
        internal const string BaseMessage = "The given cron expression has an invalid format.";

        /// <summary>
        /// Initializes a new instance of the <see cref="CronFormatException"/> class.
        /// </summary>
        public CronFormatException() : this(BaseMessage)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CronFormatException"/> class with
        /// a specified error message.
        /// </summary>
        public CronFormatException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CronFormatException"/> class with
        /// a specified error message and a reference to the inner exception that is the
        /// cause of this exception.
        /// </summary>
        public CronFormatException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

#if !NETSTANDARD1_0
        /// <inheritdoc />
#pragma warning disable SYSLIB0051
        protected CronFormatException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
#pragma warning restore SYSLIB0051
#endif
    }
}
