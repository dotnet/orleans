using System;
using System.Collections.Generic;
using System.IO;

namespace Orleans.CodeGeneration
{
    [Serializable]
    public class CodeGenOptions
    {
        public FileInfo InputAssembly;

        public List<string> ReferencedAssemblies = new List<string>();

        public string OutputFileName;
    }
}