using System;
using System.IO;
using Newtonsoft.Json;

namespace ProtoTestTool
{
    public class WorkspaceConfig
    {
        public string ProtoFolderPath { get; set; } = "";
        public string TargetIp { get; set; } = "127.0.0.1";
        public int TargetPort { get; set; } = 9000;
        
        // Proxy settings (optional but good to have)
        public int ProxyLocalPort { get; set; } = 9000;
        public string ProxyTargetIp { get; set; } = "127.0.0.1";
        public int ProxyTargetPort { get; set; } = 9001;

        private const string ConfigFileName = "workspace_config.json";

        public static WorkspaceConfig Load(string workspaceDir)
        {
            if (string.IsNullOrWhiteSpace(workspaceDir) || !Directory.Exists(workspaceDir)) return new WorkspaceConfig();

            var path = Path.Combine(workspaceDir, ConfigFileName);
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<WorkspaceConfig>(json) ?? new WorkspaceConfig();
                }
                catch { }
            }
            return new WorkspaceConfig();
        }

        public void Save(string workspaceDir)
        {
             if (string.IsNullOrWhiteSpace(workspaceDir) || !Directory.Exists(workspaceDir)) return;

             try
             {
                 var path = Path.Combine(workspaceDir, ConfigFileName);
                 var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                 File.WriteAllText(path, json);
             }
             catch { }
        }
    }
}
