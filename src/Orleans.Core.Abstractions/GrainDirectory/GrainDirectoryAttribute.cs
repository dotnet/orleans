using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.GrainDirectory
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GrainDirectoryAttribute : Attribute
    {
        public const string DEFAULT_GRAIN_DIRECTORY = "default";

        public string GrainDirectoryName { get; set; }

        public GrainDirectoryAttribute()
        {
            this.GrainDirectoryName = DEFAULT_GRAIN_DIRECTORY;
        }
    }
}
