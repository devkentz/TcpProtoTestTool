using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace ProtoTestTool.Services
{
    /// <summary>
    /// Unloadable AssemblyLoadContext for hot-reloading assemblies
    /// </summary>
    public class UnloadableAssemblyContext : AssemblyLoadContext
    {
        public UnloadableAssemblyContext() : base(isCollectible: true)
        {
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Return null to fall back to default context for dependencies
            return null;
        }

        /// <summary>
        /// Load assembly from file path with proper stream handling
        /// </summary>
        public Assembly LoadFromFile(string path)
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);

            var pdbPath = Path.ChangeExtension(path, ".pdb");
            if (File.Exists(pdbPath))
            {
                var pdbBytes = File.ReadAllBytes(pdbPath);
                using var pdbStream = new MemoryStream(pdbBytes);
                return LoadFromStream(stream, pdbStream);
            }

            return LoadFromStream(stream);
        }
    }
}
