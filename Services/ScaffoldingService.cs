using System;
using System.IO;
using System.Threading.Tasks;

namespace ProtoTestTool.Services
{
    public class ScaffoldingService
    {
        public async Task InitializeWorkspaceAsync(string workspacePath)
        {
            if (!Directory.Exists(workspacePath))
            {
                Directory.CreateDirectory(workspacePath);
            }

            var scriptsDir = Path.Combine(workspacePath, BuildConstants.ScriptsFolder);
            var protosDir = Path.Combine(workspacePath, BuildConstants.ProtosFolder);
            var libsDir = Path.Combine(scriptsDir, BuildConstants.LibsFolder);

            Directory.CreateDirectory(scriptsDir);
            Directory.CreateDirectory(protosDir);
            Directory.CreateDirectory(libsDir);

            await CreateFileIfNotExists(Path.Combine(protosDir, BuildConstants.FileNameReadme),
                BuildConstants.ReadmeContent);

            // Create Default Script Templates
            await CreateFileIfNotExists(Path.Combine(scriptsDir, BuildConstants.FileNamePacketCodec), ScriptTemplateFactory.GetTemplate(BuildConstants.TemplatePacketCodec));
            await CreateFileIfNotExists(Path.Combine(scriptsDir, BuildConstants.FileNamePacketRegistry), ScriptTemplateFactory.GetTemplate(BuildConstants.TemplatePacketRegistry));
            await CreateFileIfNotExists(Path.Combine(scriptsDir, BuildConstants.FileNamePacketHeader), ScriptTemplateFactory.GetTemplate(BuildConstants.TemplatePacketHeader));
        }

        private async Task CreateFileIfNotExists(string path, string content)
        {
            if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(path, content);
            }
        }
    }
}
