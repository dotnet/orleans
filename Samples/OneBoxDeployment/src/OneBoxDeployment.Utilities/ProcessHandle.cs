using System;
using System.Diagnostics;

namespace OneBoxDeployment.Utilities
{
    /// <summary>
    /// A handle to a process allocated by <see cref="PlatformUtilities.StartProcessWithLogging(ProcessStartInfo)"/>.
    /// </summary>
    public sealed class ProcessHandle
    {
        /// <summary>
        /// The process.
        /// </summary>
        public Process Process { get; }

        /// <summary>
        /// The standard output of the process.
        /// </summary>
        public IObservable<string> Output { get; }

        /// <summary>
        /// The error output of the process.
        /// </summary>
        public IObservable<string> ErrorOutput { get; }


        /// <summary>
        /// Default constructor.
        /// </summary>
        /// <param name="process">The process.</param>
        /// <param name="output">The standard output of the process.</param>
        /// <param name="errorOutput">The error output of the process.</param>
        public ProcessHandle(Process process, IObservable<string> output, IObservable<string> errorOutput)
        {
            Process = process ?? throw new ArgumentNullException(nameof(process));
            Output = output ?? throw new ArgumentNullException(nameof(output));
            ErrorOutput = errorOutput ?? throw new ArgumentNullException(nameof(errorOutput));
        }
    }
}
