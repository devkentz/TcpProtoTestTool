using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ProtoTestTool
{
    public class NuGetPackageInfo
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string Authors { get; set; } = "";
        public long BuildDownloads { get; set; }
    }

    public class NuGetClient
    {
        private readonly HttpClient _httpClient;
        
        public NuGetClient()
        {
            _httpClient = new HttpClient();
        }

        public async Task<List<NuGetPackageInfo>> SearchAsync(string query, int take = 20)
        {
            try
            {
                var url = $"https://azuresearch-usnc.nuget.org/query?q={Uri.EscapeDataString(query)}&take={take}&prerelease=false";
                var json = await _httpClient.GetStringAsync(url);
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    var results = new List<NuGetPackageInfo>();
                    foreach (var item in data.EnumerateArray())
                    {
                        var info = new NuGetPackageInfo
                        {
                            Id = item.GetProperty("id").GetString() ?? "",
                            Version = item.GetProperty("version").GetString() ?? "",
                            Description = item.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                            Authors = item.TryGetProperty("authors", out var authors) ? (authors.ValueKind == JsonValueKind.Array ? string.Join(", ", authors.EnumerateArray()) : authors.GetString() ?? "") : "",
                            BuildDownloads = item.TryGetProperty("totalDownloads", out var dl) ? dl.GetInt64() : 0
                        };
                        results.Add(info);
                    }
                    return results;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NuGet Search Error: {ex.Message}");
            }
            return new List<NuGetPackageInfo>();
        }

        public async Task InstallPackageAsync(string packageId, string version, string workspacePath)
        {
            // 1. Download .nupkg
            // https://api.nuget.org/v3-flatcontainer/{ID}/{VERSION}/{ID}.{VERSION}.nupkg
            var lowerId = packageId.ToLowerInvariant();
            var lowerVer = version.ToLowerInvariant();
            var url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVer}/{lowerId}.{lowerVer}.nupkg";

            var nupkgData = await _httpClient.GetByteArrayAsync(url);
            
            // 2. Extract
            var libsDir = Path.Combine(workspacePath, "Libs");
            Directory.CreateDirectory(libsDir);

            using var ms = new MemoryStream(nupkgData);
            using var archive = new ZipArchive(ms);

            // Strategy: Find best "lib/" folder.
            // Priority: net9.0 > net8.0 > net7.0 > net6.0 > netstandard2.1 > netstandard2.0
            
            var libEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("lib/") && e.Name.EndsWith(".dll"))
                .ToList();

            if (!libEntries.Any()) return; // No DLLs?

            // Group by TFM
            var tfmGroups = libEntries.GroupBy(e => 
            {
                var parts = e.FullName.Split('/');
                if (parts.Length > 1) return parts[1];
                return "unknown";
            }).ToList();

            string? bestTfm = SelectBestTfm(tfmGroups.Select(g => g.Key));
            if (bestTfm == null) 
            {
                // Fallback to any?
                throw new Exception("No compatible framework found (net6.0+ or netstandard2.0+)");
            }

            foreach (var entry in tfmGroups.First(g => g.Key == bestTfm))
            {
                var destPath = Path.Combine(libsDir, entry.Name);
                if (File.Exists(destPath)) File.Delete(destPath); // Overwrite
                entry.ExtractToFile(destPath);
            }
        }

        private string? SelectBestTfm(IEnumerable<string> tfms)
        {
            // Simple priority matching
            var priorities = new[] { "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0" };
            foreach (var p in priorities)
            {
                if (tfms.Contains(p, StringComparer.OrdinalIgnoreCase)) return p;
            }
            return null;
        }
    }
}
