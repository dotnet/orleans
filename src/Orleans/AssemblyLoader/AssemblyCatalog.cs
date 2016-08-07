using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime
{
    public class AssemblyCatalog : IAssemblyCatalog
    {
        private HashSet<Assembly> catalog = new HashSet<Assembly>();
        private Logger logger;

        public AssemblyCatalog()
        {
            logger = LogManager.GetLogger("AssemblyCatalog", LoggerType.Provider);
        }

        public List<Assembly> GetAssemblies()
        {
            foreach (var asm in catalog.ToList())
            {
                LoadDependencies(asm, asm.GetReferencedAssemblies());
            }
            return catalog.ToList();
        }

        public AssemblyCatalog WithAssembly(string assemblyName)
        {
            try
            {
                var location = new FileInfo(assemblyName).FullName;

                catalog.Add(Assembly.LoadFrom(location));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unable to load assembly: {assemblyName}", ex);
            }
            
            return this;            
        }

        private void LoadDependencies(Assembly fromAssembly, AssemblyName[] dependencies)
        {
            foreach (var reference in dependencies)
            {
                try
                {
                    var asm = Assembly.Load(reference);
                    if (catalog.Add(asm))
                    {
                        LoadDependencies(asm, asm.GetReferencedAssemblies());
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ErrorCode.Provider_AssemblyLoadError, $"Unable to load assembly {reference.FullName} referenced by {fromAssembly.FullName}", ex);
                }
            }            
        }
    }
}
