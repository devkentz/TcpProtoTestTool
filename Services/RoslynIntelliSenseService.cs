using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace ProtoTestTool.Services;

public class RoslynIntelliSenseService
{
    private AdhocWorkspace? _workspace;
    private ProjectId? _projectId;
    private readonly Dictionary<string, DocumentId> _documents = new();
    private readonly List<MetadataReference> _references = [];

    public Task InitializeAsync(string workspacePath, IEnumerable<string>? additionalReferences = null)
    {
        // Create workspace with MEF host including Features assemblies
        var assemblies = MefHostServices.DefaultAssemblies
            .Add(typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly)
            .Add(typeof(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions).Assembly);
        var host = MefHostServices.Create(assemblies);
        _workspace = new AdhocWorkspace(host);

        // Build references
        BuildReferences(workspacePath, additionalReferences);

        // Create project
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "ScriptProject",
            "ScriptProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: _references);

        var project = _workspace.AddProject(projectInfo);
        _projectId = project.Id;

        return Task.CompletedTask;
    }

    private void BuildReferences(string workspacePath, IEnumerable<string>? additionalReferences)
    {
        _references.Clear();
        var addedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add trusted platform assemblies (BCL)
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(trustedAssemblies))
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (addedAssemblies.Contains(name)) continue;

                try
                {
                    _references.Add(MetadataReference.CreateFromFile(path));
                    addedAssemblies.Add(name);
                }
                catch { }
            }
        }

        // Add Google.Protobuf
        AddReference(typeof(Google.Protobuf.IMessage).Assembly.Location, addedAssemblies);

        // Add ProtoTestTool.ScriptContract
        AddReference(typeof(ScriptContract.ScriptGlobals).Assembly.Location, addedAssemblies);

        // Add Protos.dll if exists
        var protosDll = Path.Combine(workspacePath, "Protos.dll");
        if (File.Exists(protosDll))
        {
            AddReference(protosDll, addedAssemblies);
        }

        // Add additional references (NuGet packages, etc.)
        if (additionalReferences != null)
        {
            foreach (var refPath in additionalReferences)
            {
                if (File.Exists(refPath))
                    AddReference(refPath, addedAssemblies);
            }
        }
    }

    private void AddReference(string path, HashSet<string> added)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (added.Contains(name)) return;

        try
        {
            _references.Add(MetadataReference.CreateFromFile(path));
            added.Add(name);
        }
        catch { }
    }

    public void UpdateDocument(string fileName, string content)
    {
        if (_workspace == null || _projectId == null) return;

        var project = _workspace.CurrentSolution.GetProject(_projectId);
        if (project == null) return;

        if (_documents.TryGetValue(fileName, out var existingDocId))
        {
            // Update existing document
            var doc = _workspace.CurrentSolution.GetDocument(existingDocId);
            if (doc != null)
            {
                var newSolution = _workspace.CurrentSolution.WithDocumentText(
                    existingDocId, SourceText.From(content));
                _workspace.TryApplyChanges(newSolution);
            }
        }
        else
        {
            // Add new document
            var docInfo = DocumentInfo.Create(
                DocumentId.CreateNewId(_projectId),
                fileName,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(content), VersionStamp.Create())),
                filePath: fileName);

            var newSolution = _workspace.CurrentSolution.AddDocument(docInfo);
            _workspace.TryApplyChanges(newSolution);
            _documents[fileName] = docInfo.Id;
        }
    }

    public async Task<List<CompletionItemDto>> GetCompletionsAsync(string fileName, int position)
    {
        var result = new List<CompletionItemDto>();

        System.Diagnostics.Debug.WriteLine($"[Roslyn] GetCompletionsAsync: {fileName}, pos={position}");

        if (_workspace == null)
        {
            System.Diagnostics.Debug.WriteLine("[Roslyn] Workspace is null");
            return result;
        }

        if (!_documents.TryGetValue(fileName, out var docId))
        {
            System.Diagnostics.Debug.WriteLine($"[Roslyn] Document not found: {fileName}");
            System.Diagnostics.Debug.WriteLine($"[Roslyn] Available docs: {string.Join(", ", _documents.Keys)}");
            return result;
        }

        var document = _workspace.CurrentSolution.GetDocument(docId);
        if (document == null)
        {
            System.Diagnostics.Debug.WriteLine("[Roslyn] Document is null from solution");
            return result;
        }

        try
        {
            var completionService = document.Project.Solution.Workspace.Services
                .GetLanguageServices(document.Project.Language)
                .GetService<Microsoft.CodeAnalysis.Completion.CompletionService>();

            if (completionService == null)
            {
                System.Diagnostics.Debug.WriteLine("[Roslyn] CompletionService is null!");
                return result;
            }

            System.Diagnostics.Debug.WriteLine("[Roslyn] Getting completions...");
            var completions = await completionService.GetCompletionsAsync(document, position);
            if (completions == null)
            {
                System.Diagnostics.Debug.WriteLine("[Roslyn] Completions result is null");
                return result;
            }

            System.Diagnostics.Debug.WriteLine($"[Roslyn] Got {completions.ItemsList.Count} raw completions");

            foreach (var item in completions.ItemsList.Take(100)) // Limit for performance
            {
                result.Add(new CompletionItemDto
                {
                    Label = item.DisplayText,
                    Kind = GetMonacoKind(item.Tags),
                    InsertText = item.DisplayText,
                    Detail = string.Join(", ", item.Tags),
                    FilterText = item.FilterText
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Roslyn] Completion error: {ex.Message}");
        }

        return result;
    }

    public async Task<List<DiagnosticDto>> GetDiagnosticsAsync(string fileName)
    {
        var result = new List<DiagnosticDto>();

        if (_workspace == null || !_documents.TryGetValue(fileName, out var docId))
            return result;

        var document = _workspace.CurrentSolution.GetDocument(docId);
        if (document == null) return result;

        try
        {
            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null) return result;

            var diagnostics = semanticModel.GetDiagnostics();
            foreach (var diag in diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning))
            {
                var span = diag.Location.GetLineSpan();
                result.Add(new DiagnosticDto
                {
                    StartLine = span.StartLinePosition.Line + 1,
                    StartColumn = span.StartLinePosition.Character + 1,
                    EndLine = span.EndLinePosition.Line + 1,
                    EndColumn = span.EndLinePosition.Character + 1,
                    Message = diag.GetMessage(),
                    Severity = diag.Severity == DiagnosticSeverity.Error ? 8 : 4 // Monaco: Error=8, Warning=4
                });
            }
        }
        catch { }

        return result;
    }

    public async Task<SignatureHelpDto?> GetSignatureHelpAsync(string fileName, int position)
    {
        if (_workspace == null || !_documents.TryGetValue(fileName, out var docId))
            return null;

        var document = _workspace.CurrentSolution.GetDocument(docId);
        if (document == null) return null;

        try
        {
            var text = await document.GetTextAsync();
            var syntaxTree = await document.GetSyntaxTreeAsync();
            var semanticModel = await document.GetSemanticModelAsync();

            if (syntaxTree == null || semanticModel == null) return null;

            var root = await syntaxTree.GetRootAsync();
            var token = root.FindToken(position);

            // Find invocation expression
            var invocation = token.Parent?.AncestorsAndSelf()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                .FirstOrDefault();

            if (invocation == null) return null;

            var symbolInfo = semanticModel.GetSymbolInfo(invocation.Expression);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

            if (methodSymbol == null) return null;

            var parameters = methodSymbol.Parameters.Select(p => new ParameterDto
            {
                Label = $"{p.Type.ToDisplayString()} {p.Name}",
                Documentation = ""
            }).ToList();

            return new SignatureHelpDto
            {
                Label = methodSymbol.ToDisplayString(),
                Documentation = "",
                Parameters = parameters
            };
        }
        catch { }

        return null;
    }

    private static int GetMonacoKind(ImmutableArray<string> tags)
    {
        // Monaco CompletionItemKind
        if (tags.Contains("Method")) return 0;
        if (tags.Contains("Property")) return 9;
        if (tags.Contains("Field")) return 4;
        if (tags.Contains("Class")) return 5;
        if (tags.Contains("Interface")) return 7;
        if (tags.Contains("Struct")) return 22;
        if (tags.Contains("Enum")) return 15;
        if (tags.Contains("EnumMember")) return 16;
        if (tags.Contains("Keyword")) return 17;
        if (tags.Contains("Namespace")) return 18;
        if (tags.Contains("Local") || tags.Contains("Parameter")) return 5;
        return 0; // Text
    }
}

public class CompletionItemDto
{
    public string Label { get; set; } = "";
    public int Kind { get; set; }
    public string InsertText { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? FilterText { get; set; }
}

public class DiagnosticDto
{
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public string Message { get; set; } = "";
    public int Severity { get; set; }
}

public class SignatureHelpDto
{
    public string Label { get; set; } = "";
    public string Documentation { get; set; } = "";
    public List<ParameterDto> Parameters { get; set; } = [];
}

public class ParameterDto
{
    public string Label { get; set; } = "";
    public string Documentation { get; set; } = "";
}
