using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ProtoTestTool.Network;

namespace ProtoTestTool.Services
{
    /// <summary>
    /// Manages the AssemblyLoadContext for Proto and Script assemblies.
    /// Ensures both are loaded into the same unloadable context to share types.
    /// </summary>
    public class AssemblyContextManager
    {
        private UnloadableAssemblyContext? _context;

        public Assembly? ProtoAssembly { get; private set; }
        public Assembly? ScriptAssembly { get; private set; }

        public bool IsLoaded => _context != null;

        /// <summary>
        /// Unloads the current context and clears all references.
        /// </summary>
        public void Unload()
        {
            if (_context != null)
            {
                // Clear References
                ProtoLoaderManager.Instance.Clear();
                ProtoAssembly = null;
                ScriptAssembly = null;

                // Unload Context
                _context.Unload();
                _context = null;

                // Force GC
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        /// <summary>
        /// Ensures a context exists, then loads the Proto assembly.
        /// </summary>
        public Assembly LoadProtoAssembly(string dllPath)
        {
            EnsureContext();

            if (ProtoAssembly != null)
                throw new InvalidOperationException("ProtoAssembly is already loaded. Unload first.");

            if (!File.Exists(dllPath))
                throw new FileNotFoundException("Proto DLL not found", dllPath);

            ProtoAssembly = _context!.LoadFromFile(dllPath);

            var messageTypes = ProtobufHelper.GetIMessageTypes(ProtoAssembly);
            ProtoLoaderManager.Instance.InitPacket(messageTypes);
            return ProtoAssembly;
        }


        /// <summary>
        /// Ensures a context exists, then loads the Script assembly.
        /// </summary>
        public Assembly LoadScriptAssembly(string dllPath)
        {
            EnsureContext();

            if (ScriptAssembly != null)
                throw new InvalidOperationException("ScriptAssembly is already loaded. Unload first.");

            if (!File.Exists(dllPath))
                throw new FileNotFoundException("Script DLL not found", dllPath);

            ScriptAssembly = _context!.LoadFromFile(dllPath);
            return ScriptAssembly;
        }

        private void EnsureContext()
        {
            if (_context == null)
            {
                _context = new UnloadableAssemblyContext();
            }
        }
    }
}