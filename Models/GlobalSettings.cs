using System.IO;
using Newtonsoft.Json;

namespace ProtoTestTool.Models
{
    public class GlobalSettings
    {
        private const string FileName = "global_settings.json";

        public static string AppDataDir { get; } =
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;

        public List<string> RecentWorkspaces { get; set; } = new List<string>();

        public static GlobalSettings Load()
        {
            try
            {
                var path = Path.Combine(AppDataDir, FileName);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<GlobalSettings>(json) ?? new GlobalSettings();
                }
            }
            catch
            {
                // ignored
            }

            return new GlobalSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                var path = Path.Combine(AppDataDir, FileName);
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch
            {
                // ignored
            }
        }

        public void AddRecent(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            
            // Remove existing to re-insert at top
            RecentWorkspaces.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            RecentWorkspaces.Insert(0, path);

            // Keep only last 10
            if (RecentWorkspaces.Count > 10)
            {
                RecentWorkspaces = RecentWorkspaces.Take(10).ToList();
            }
            
            Save();
        }

        public void RemoveRecent(string path)
        {
             RecentWorkspaces.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
             Save();
        }
    }
}
