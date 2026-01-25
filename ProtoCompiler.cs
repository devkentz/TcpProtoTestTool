using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ProtoTestTool
{
    public class ProtoCompiler
    {
        private string _protocPath = "protoc"; 
        private string _includePath = null!;

        public ProtoCompiler()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _protocPath = Path.Combine(baseDir, "Tools", "protoc.exe");
            _includePath = Path.Combine(baseDir, "Tools"); // Should contain google/protobuf
            
            if (!File.Exists(_protocPath))
            {
                // Fallback specific loop for debugging environment vs deployed
                var commonPaths = new[]
                {
                    Path.Combine(baseDir, "..", "..", "..", "Tools", "protoc.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "grpc.tools", "2.60.0", "tools", "windows_x64", "protoc.exe")
                };
                
                foreach(var path in commonPaths)
                {
                    if (File.Exists(path))
                    {
                        _protocPath = path;
                        _includePath = Path.GetDirectoryName(Path.GetDirectoryName(path)) ?? ""; // tools folder
                        break;
                    }
                }
            }
        }

        public string CompileProtoToCSharp(string protoPath, string outputDir)
        {
            if (!File.Exists(_protocPath))
                throw new FileNotFoundException($"protoc.exe not found at {_protocPath}");

            if (!File.Exists(protoPath))
                throw new FileNotFoundException($"Proto file not found: {protoPath}");

            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            // Determine proto directory for import resolution
            var protoDir = Path.GetDirectoryName(protoPath);
            
            // Output file prediction (ProtoName.cs) - simple heuristic, protoc does ConvertToPascalCase
            // We'll trust protoc just writes it.

            var arguments = $"--csharp_out=\"{outputDir}\" --proto_path=\"{protoDir}\" --proto_path=\"{_includePath}\" \"{protoPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = _protocPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("Failed to start protoc.");
            var output = process.StandardOutput!.ReadToEnd();
            var error = process.StandardError!.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"protoc failed (ExitCode {process.ExitCode}):{Environment.NewLine}{error}{Environment.NewLine}{output}");
            }
            
            // Find generated file. 
            // Protoc generates based on 'option csharp_namespace' or filename.
            // We just return one or all cs files in outputDir that are new?
            // For simplicity, let's assume we output to a clean temp dir, so all .cs files are ours.
            return outputDir; 
        }
    }
}
