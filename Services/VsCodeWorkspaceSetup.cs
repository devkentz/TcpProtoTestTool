using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ProtoTestTool.Services;

public static class VsCodeWorkspaceSetup
{
    private static readonly Regex GeneratedDllPattern = new(@".+\.[a-fA-F0-9]{8}\.dll$", RegexOptions.Compiled);

    /// <summary>
    /// Creates or updates all VS Code workspace files (.csproj, .vscode/settings.json, .vscode/extensions.json).
    /// </summary>
    public static void EnsureWorkspaceSetup(string workspacePath)
    {
        var scriptsDir = Path.Combine(workspacePath, "Scripts");
        if (!Directory.Exists(scriptsDir)) return;

        EnsureVsCodeSettings(scriptsDir);
        EnsureExtensionsJson(scriptsDir);
        UpdateProjectFile(workspacePath);
    }

    /// <summary>
    /// Updates Scripts.csproj with current DLL references (call after build or proto compilation).
    /// </summary>
    public static void UpdateProjectReferences(string workspacePath)
    {
        UpdateProjectFile(workspacePath);
    }

    private static void UpdateProjectFile(string workspacePath)
    {
        var scriptsDir = Path.Combine(workspacePath, "Scripts");
        var csprojPath = Path.Combine(scriptsDir, "Scripts.csproj");

        var references = CollectDllReferences(workspacePath, scriptsDir);

        var project = new XElement("Project",
            new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net9.0"),
                new XElement("EnableDefaultCompileItems", "true"),
                new XElement("ImplicitUsings", "enable"),
                new XElement("Nullable", "enable"),
                // Suppress warnings for script-style code
                new XElement("NoWarn", "CS5001;CS0028")
            )
        );

        if (references.Count > 0)
        {
            var itemGroup = new XElement("ItemGroup");
            foreach (var refPath in references)
            {
                var assemblyName = Path.GetFileNameWithoutExtension(refPath);
                // Use relative path from Scripts folder
                var relativePath = Path.GetRelativePath(scriptsDir, refPath);

                itemGroup.Add(new XElement("Reference",
                    new XAttribute("Include", assemblyName),
                    new XElement("HintPath", relativePath)
                ));
            }
            project.Add(itemGroup);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), project);
        File.WriteAllText(csprojPath, doc.ToString());
    }

    private static List<string> CollectDllReferences(string workspacePath, string scriptsDir)
    {
        var refs = new List<string>();

        // Workspace root DLLs (ScriptContract.dll, Google.Protobuf.dll, etc.)
        if (Directory.Exists(workspacePath))
        {
            foreach (var dll in Directory.GetFiles(workspacePath, "*.dll"))
            {
                var fileName = Path.GetFileName(dll);
                if (!GeneratedDllPattern.IsMatch(dll) &&
                    !fileName.Equals("Script.dll", StringComparison.OrdinalIgnoreCase))
                {
                    refs.Add(dll);
                }
            }
        }

        // Libs/ folder DLLs
        var libsDir = Path.Combine(scriptsDir, "Libs");
        if (Directory.Exists(libsDir))
        {
            refs.AddRange(Directory.GetFiles(libsDir, "*.dll", SearchOption.AllDirectories));
        }

        // ProtoGen/ folder DLLs
        var protoGenDir = Path.Combine(workspacePath, "ProtoGen");
        if (Directory.Exists(protoGenDir))
        {
            foreach (var dll in Directory.GetFiles(protoGenDir, "*.dll", SearchOption.AllDirectories))
            {
                if (!refs.Contains(dll) && !GeneratedDllPattern.IsMatch(dll))
                    refs.Add(dll);
            }
        }

        return refs;
    }

    private static void EnsureVsCodeSettings(string scriptsDir)
    {
        var vscodePath = Path.Combine(scriptsDir, ".vscode");
        if (!Directory.Exists(vscodePath)) Directory.CreateDirectory(vscodePath);

        var settingsPath = Path.Combine(vscodePath, "settings.json");
        if (File.Exists(settingsPath)) return;

        var settings = new Dictionary<string, object>
        {
            ["files.autoSave"] = "afterDelay",
            ["files.autoSaveDelay"] = 500,
            ["editor.formatOnSave"] = true,
            ["editor.bracketPairColorization.enabled"] = true,
            ["editor.guides.bracketPairs"] = true,
            ["editor.stickyScroll.enabled"] = true,
            ["editor.minimap.enabled"] = true,
            ["editor.cursorBlinking"] = "smooth",
            ["editor.smoothScrolling"] = true,
            ["omnisharp.enableRoslynAnalyzers"] = true,
            ["dotnet.defaultSolution"] = "Scripts.csproj"
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json);
    }

    private static void EnsureExtensionsJson(string scriptsDir)
    {
        var vscodePath = Path.Combine(scriptsDir, ".vscode");
        if (!Directory.Exists(vscodePath)) Directory.CreateDirectory(vscodePath);

        var extensionsPath = Path.Combine(vscodePath, "extensions.json");
        if (File.Exists(extensionsPath)) return;

        var extensions = new
        {
            recommendations = new[]
            {
                "ms-dotnettools.csharp",
                "ms-dotnettools.csdevkit"
            }
        };

        var json = JsonSerializer.Serialize(extensions, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(extensionsPath, json);
    }
}
