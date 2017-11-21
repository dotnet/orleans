using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Orleans.CodeGeneration
{
    [Serializable]
    public class CodeGenOptions
    {
        public FileInfo InputAssembly;

        public List<string> ReferencedAssemblies = new List<string>();

        public string OutputFileName;
        public LogLevel LogLevel { get; set; } = LogLevel.Warning;
    }
}