using System;
using System.Reflection;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Represents a generated assembly.
    /// </summary>
    [Serializable]
    public class GeneratedAssembly
    {
        [NonSerialized]
        private Assembly assembly;

        /// <summary>
        /// Initializes a new instance of the <see cref="GeneratedAssembly"/> class.
        /// </summary>
        public GeneratedAssembly()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GeneratedAssembly"/> class.
        /// </summary>
        /// <param name="other">The other instance.</param>
        public GeneratedAssembly(GeneratedAssembly other)
        {
            this.RawBytes = other.RawBytes;
            this.DebugSymbolRawBytes = other.DebugSymbolRawBytes;
            this.Assembly = other.Assembly;
        }

        /// <summary>
        /// Gets or sets a serialized representation of the assembly.
        /// </summary>
        public byte[] RawBytes { get; set; }

        /// <summary>
        /// Gets or sets a serialized representation of the assembly's debug symbol stream.
        /// </summary>
        public byte[] DebugSymbolRawBytes { get; set; }

        /// <summary>
        /// Gets or sets the assembly.
        /// </summary>
        public Assembly Assembly
        {
            get { return this.assembly; }
            set { this.assembly = value; }
        }
    }
} 