using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynCompletionService = Microsoft.CodeAnalysis.Completion.CompletionService;

namespace ProtoTestTool.Services;

public class RoslynIntelliSenseService
{
    private AdhocWorkspace? _workspace;
    private ProjectId? _projectId;
    private readonly Dictionary<string, DocumentId> _documents = new();
    private readonly List<MetadataReference> _references = [];

    public Task InitializeAsync(string workspacePath, IEnumerable<string>? additionalReferences = null)
    {
        // Load all Roslyn related assemblies explicitly to ensure MEF composition works
        var assemblies = new List<System.Reflection.Assembly>
        {
            typeof(Microsoft.CodeAnalysis.Host.Mef.MefHostServices).Assembly,
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly,
            typeof(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions).Assembly,
            typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly,
        };

        try
        {
            // Try to load CSharp Features assembly (where C# CompletionProvider lives)
             assemblies.Add(System.Reflection.Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"));
             assemblies.Add(System.Reflection.Assembly.Load("Microsoft.CodeAnalysis.Features"));
        }
        catch { /* Ignore if not found, but it likely won't work well without them */ }
        
        // Add default assemblies as well
        var allAssemblies = MefHostServices.DefaultAssemblies.Concat(assemblies).Distinct();

        var host = MefHostServices.Create(allAssemblies);
        _workspace = new AdhocWorkspace(host);

        BuildReferences(workspacePath, additionalReferences);
        
        // Project initialization...
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "ScriptProject",
            "ScriptProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithUsings("System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Threading.Tasks", "ProtoTestTool.ScriptContract"),
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

        // 1. Core Framework Assemblies (Essential for basic Types)
        var coreTypes = new[]
        {
            typeof(object), // System.Private.CoreLib
            typeof(Uri), // System.Private.Uri
            typeof(Console), // System.Console
            typeof(System.Linq.Enumerable), // System.Linq
            typeof(System.Collections.Generic.List<>), // System.Collections
        };

        foreach (var type in coreTypes)
        {
            AddReference(type.Assembly.Location, addedAssemblies);
        }

        // 2. Try TRUSTED_PLATFORM_ASSEMBLIES
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(trustedAssemblies))
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                AddReference(path, addedAssemblies);
            }
        }

        // 3. AppDomain Fallback (Catch-all for runtime assemblies)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
            // Filter out obviously irrelevant assemblies if needed, but for now include all
            AddReference(asm.Location, addedAssemblies);
        }

        // 4. Explicit Application Dependencies (The Fix for ScriptGlobals)
        // Ensure the main application assembly is referenced so scripts can see ScriptGlobals, etc.
        // typeof(App) is in ProtoTestTool
        AddReference(typeof(App).Assembly.Location, addedAssemblies); 
        AddReference(typeof(ScriptContract.ScriptGlobals).Assembly.Location, addedAssemblies);
        AddReference(typeof(Google.Protobuf.IMessage).Assembly.Location, addedAssemblies);

        // 5. Workspace Protos
        var protosDll = Path.Combine(workspacePath, "Protos.dll");
        if (File.Exists(protosDll))
            AddReference(protosDll, addedAssemblies);

        // 6. Additional References (Libs)
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
        
        // Use full path as key to allow multiple versions of same assembly name if needed,
        // but for now, simple name check is enough to avoid obvious dupes.
        var name = Path.GetFileNameWithoutExtension(path);
        if (added.Contains(name)) return;

        try
        {
            _references.Add(MetadataReference.CreateFromFile(path));
            added.Add(name);
        }
        catch
        {
            // Fallback: Read into memory if file is locked
            try
            {
                var bytes = File.ReadAllBytes(path);
                using var stream = new MemoryStream(bytes);
                // CreateFromStream reads metadata immediately
                var reference = MetadataReference.CreateFromStream(stream); 
                _references.Add(reference);
                added.Add(name);
            }
            catch { /* Give up */ }
        }
    }

    public void UpdateDocument(string fileName, string content)
    {
        if (_workspace == null || _projectId == null) return;

        if (_documents.TryGetValue(fileName, out var existingDocId))
        {
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

    private Document? GetDocument(string fileName)
    {
        if (_workspace == null || !_documents.TryGetValue(fileName, out var docId))
            return null;
        return _workspace.CurrentSolution.GetDocument(docId);
    }

    // ========== Completions ==========

    public async Task<List<CompletionItemDto>> GetCompletionsAsync(string fileName, int position)
    {
        var result = new List<CompletionItemDto>();
        var document = GetDocument(fileName);
        if (document == null) return result;

        try
        {
            var completionService = RoslynCompletionService.GetService(document);
            if (completionService == null) return result;

            var completions = await completionService.GetCompletionsAsync(document, position);
            if (completions == null) return result;

            // Increase limit to avoid cutting off valid suggestions when filtering hasn't narrowed down enough yet.
            foreach (var item in completions.ItemsList)
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
        catch { }

        return result;
    }

    // ========== Diagnostics ==========

    public async Task<List<DiagnosticDto>> GetDiagnosticsAsync(string fileName)
    {
        var result = new List<DiagnosticDto>();
        var document = GetDocument(fileName);
        if (document == null) return result;

        try
        {
            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null) return result;

            foreach (var diag in semanticModel.GetDiagnostics()
                .Where(d => d.Severity >= DiagnosticSeverity.Warning))
            {
                var span = diag.Location.GetLineSpan();
                result.Add(new DiagnosticDto
                {
                    StartLine = span.StartLinePosition.Line + 1,
                    StartColumn = span.StartLinePosition.Character + 1,
                    EndLine = span.EndLinePosition.Line + 1,
                    EndColumn = span.EndLinePosition.Character + 1,
                    Message = diag.GetMessage(),
                    Severity = diag.Severity == DiagnosticSeverity.Error ? 8 : 4
                });
            }
        }
        catch { }

        return result;
    }

    // ========== Signature Help ==========

    public async Task<SignatureHelpDto?> GetSignatureHelpAsync(string fileName, int position)
    {
        var document = GetDocument(fileName);
        if (document == null) return null;

        try
        {
            var syntaxTree = await document.GetSyntaxTreeAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            if (syntaxTree == null || semanticModel == null) return null;

            var root = await syntaxTree.GetRootAsync();
            var token = root.FindToken(position);

            var invocation = token.Parent?.AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault();

            if (invocation == null) return null;

            var symbolInfo = semanticModel.GetSymbolInfo(invocation.Expression);
            var bestMatch = symbolInfo.Symbol as IMethodSymbol;

            // Get all overloads
            var candidates = semanticModel.GetMemberGroup(invocation.Expression).OfType<IMethodSymbol>().ToList();

            if (!candidates.Any())
            {
                if (bestMatch != null)
                {
                    candidates.Add(bestMatch);
                }
                else
                {
                    candidates.AddRange(symbolInfo.CandidateSymbols.OfType<IMethodSymbol>());
                }
            }

            // Deduplicate
            candidates = candidates.Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>().ToList();

            if (!candidates.Any()) return null;

            var dto = new SignatureHelpDto();
            dto.ActiveSignature = 0;
            dto.ActiveParameter = 0; // Ideally calculate based on argument count/position

            // Determine active signature index
            if (bestMatch != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (SymbolEqualityComparer.Default.Equals(candidates[i], bestMatch))
                    {
                        dto.ActiveSignature = i;
                        break;
                    }
                }
            }

            foreach (var methodSymbol in candidates)
            {
                dto.Signatures.Add(new SignatureItemDto
                {
                    Label = methodSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Documentation = methodSymbol.GetDocumentationCommentXml() ?? "", // Simplified doc extraction
                    Parameters = methodSymbol.Parameters.Select(p => new ParameterDto
                    {
                        Label = $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}",
                        Documentation = ""
                    }).ToList()
                });
            }

            // Calculate active parameter (naive implementation)
            // Count commas before caret inside argument list
            var argList = invocation.ArgumentList;
            if (argList != null)
            {
                // Simple count of arguments before position? 
                // A bit complex to do perfectly, assuming 0 for now or last arg.
                // If cursor is inside args, count distinct params
                int paramIndex = 0;
                foreach(var arg in argList.Arguments)
                {
                     if (arg.Span.End < position) paramIndex++;
                }
                dto.ActiveParameter = paramIndex;
            }

            return dto;
        }
        catch { }

        return null;
    }

    // ========== Hover (QuickInfo) ==========

    public async Task<HoverDto?> GetHoverAsync(string fileName, int position)
    {
        var document = GetDocument(fileName);
        if (document == null) return null;

        try
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (semanticModel == null || syntaxTree == null) return null;

            var root = await syntaxTree.GetRootAsync();
            var token = root.FindToken(position);
            if (token.Span.Length == 0) return null;

            var symbolInfo = semanticModel.GetSymbolInfo(token.Parent!);
            var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(token.Parent!);

            if (symbol == null)
            {
                var typeInfo = semanticModel.GetTypeInfo(token.Parent!);
                if (typeInfo.Type != null)
                {
                    return new HoverDto
                    {
                        Content = typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    };
                }
                return null;
            }

            var displayString = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            var documentation = "";
            var xmlComment = symbol.GetDocumentationCommentXml();
            if (!string.IsNullOrEmpty(xmlComment))
            {
                var summaryStart = xmlComment.IndexOf("<summary>", StringComparison.Ordinal);
                var summaryEnd = xmlComment.IndexOf("</summary>", StringComparison.Ordinal);
                if (summaryStart >= 0 && summaryEnd > summaryStart)
                {
                    documentation = xmlComment
                        .Substring(summaryStart + 9, summaryEnd - summaryStart - 9)
                        .Trim();
                }
            }

            return new HoverDto
            {
                Content = displayString,
                Documentation = documentation
            };
        }
        catch { }

        return null;
    }

    // ========== Go to Definition ==========

    public async Task<LocationDto?> GetDefinitionAsync(string fileName, int position)
    {
        var document = GetDocument(fileName);
        if (document == null) return null;

        try
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (semanticModel == null || syntaxTree == null) return null;

            var root = await syntaxTree.GetRootAsync();
            var token = root.FindToken(position);

            var symbolInfo = semanticModel.GetSymbolInfo(token.Parent!);
            var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(token.Parent!);

            if (symbol == null) return null;

            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (location == null) return null;

            var lineSpan = location.GetLineSpan();

            // Find which document contains this syntax tree
            string? targetFileName = null;
            foreach (var kvp in _documents)
            {
                var doc = _workspace?.CurrentSolution.GetDocument(kvp.Value);
                if (doc == null) continue;
                var tree = await doc.GetSyntaxTreeAsync();
                if (tree == location.SourceTree)
                {
                    targetFileName = kvp.Key;
                    break;
                }
            }

            return new LocationDto
            {
                FileName = targetFileName ?? fileName,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1
            };
        }
        catch { }

        return null;
    }

    // ========== Find References ==========

    public async Task<List<LocationDto>> GetReferencesAsync(string fileName, int position)
    {
        var result = new List<LocationDto>();
        var document = GetDocument(fileName);
        if (document == null || _workspace == null) return result;

        try
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (semanticModel == null || syntaxTree == null) return result;

            var root = await syntaxTree.GetRootAsync();
            var token = root.FindToken(position);

            var symbolInfo = semanticModel.GetSymbolInfo(token.Parent!);
            var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(token.Parent!);

            if (symbol == null) return result;

            var references = await SymbolFinder.FindReferencesAsync(
                symbol, _workspace.CurrentSolution);

            foreach (var refGroup in references)
            {
                foreach (var location in refGroup.Locations)
                {
                    var lineSpan = location.Location.GetLineSpan();
                    var refDocId = location.Document.Id;

                    var refFileName = _documents
                        .FirstOrDefault(d => d.Value == refDocId).Key ?? fileName;

                    var span = location.Location.SourceSpan;

                    result.Add(new LocationDto
                    {
                        FileName = refFileName,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        Length = span.Length
                    });
                }
            }
        }
        catch { }

        return result;
    }

    // ========== Code Actions ==========
    // Code Actions use CodeFixProvider/CodeRefactoringProvider from Roslyn Features.
    // Since ICodeFixService is internal, we provide a simplified approach via diagnostics.

    public Task<List<CodeActionDto>> GetCodeActionsAsync(string fileName, int startPosition, int endPosition)
    {
        // Placeholder: Code actions require CodeFixProvider registration which is internal.
        // Real code actions will be added in a future phase using MEF composition.
        return Task.FromResult(new List<CodeActionDto>());
    }

    public Task<CodeEditDto?> ApplyCodeActionAsync(string fileName, int actionIndex)
    {
        return Task.FromResult<CodeEditDto?>(null);
    }

    // ========== Formatting ==========

    public async Task<List<TextEditDto>> FormatDocumentAsync(string fileName)
    {
        var result = new List<TextEditDto>();
        var document = GetDocument(fileName);
        if (document == null) return result;

        try
        {
            var formatted = await Formatter.FormatAsync(document);
            var originalText = await document.GetTextAsync();
            var formattedText = await formatted.GetTextAsync();

            var changes = formattedText.GetTextChanges(originalText);
            foreach (var change in changes)
            {
                var startLine = originalText.Lines.GetLineFromPosition(change.Span.Start);
                var endLine = originalText.Lines.GetLineFromPosition(change.Span.End);

                result.Add(new TextEditDto
                {
                    StartLine = startLine.LineNumber + 1,
                    StartColumn = change.Span.Start - startLine.Start + 1,
                    EndLine = endLine.LineNumber + 1,
                    EndColumn = change.Span.End - endLine.Start + 1,
                    Text = change.NewText ?? ""
                });
            }
        }
        catch { }

        return result;
    }

    // ========== Rename ==========

    public async Task<RenameResultDto?> RenameSymbolAsync(string fileName, int position, string newName)
    {
        var document = GetDocument(fileName);
        if (document == null || _workspace == null) return null;

        try
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (semanticModel == null || syntaxTree == null) return null;

            var root = await syntaxTree.GetRootAsync();
            var token = root.FindToken(position);

            var symbolInfo = semanticModel.GetSymbolInfo(token.Parent!);
            var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(token.Parent!);
            if (symbol == null) return null;

            var solution = _workspace.CurrentSolution;
            var newSolution = await Renamer.RenameSymbolAsync(
                solution, symbol, new SymbolRenameOptions(), newName);

            var result = new RenameResultDto();

            foreach (var kvp in _documents)
            {
                var docId = kvp.Value;
                var originalDoc = solution.GetDocument(docId);
                var renamedDoc = newSolution.GetDocument(docId);
                if (originalDoc == null || renamedDoc == null) continue;

                var originalText = await originalDoc.GetTextAsync();
                var renamedText = await renamedDoc.GetTextAsync();

                var changes = renamedText.GetTextChanges(originalText);
                if (changes.Count == 0) continue;

                var edits = new List<TextEditDto>();
                foreach (var change in changes)
                {
                    var startLine = originalText.Lines.GetLineFromPosition(change.Span.Start);
                    var endLine = originalText.Lines.GetLineFromPosition(change.Span.End);

                    edits.Add(new TextEditDto
                    {
                        StartLine = startLine.LineNumber + 1,
                        StartColumn = change.Span.Start - startLine.Start + 1,
                        EndLine = endLine.LineNumber + 1,
                        EndColumn = change.Span.End - endLine.Start + 1,
                        Text = change.NewText ?? ""
                    });
                }

                result.Edits.Add(new FileEditDto { FileName = kvp.Key, TextEdits = edits });
            }

            // Apply to workspace
            _workspace.TryApplyChanges(newSolution);

            return result;
        }
        catch { }

        return null;
    }

    // ========== Document Symbols ==========

    public async Task<List<SymbolDto>> GetDocumentSymbolsAsync(string fileName)
    {
        var result = new List<SymbolDto>();
        var document = GetDocument(fileName);
        if (document == null) return result;

        try
        {
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (syntaxTree == null) return result;

            var root = await syntaxTree.GetRootAsync();
            CollectSymbols(root, result);
        }
        catch { }

        return result;
    }

    private static void CollectSymbols(SyntaxNode node, List<SymbolDto> symbols)
    {
        foreach (var child in node.ChildNodes())
        {
            SymbolDto? symbol = child switch
            {
                NamespaceDeclarationSyntax ns => CreateSymbol(ns.Name.ToString(), 2, ns),
                FileScopedNamespaceDeclarationSyntax ns => CreateSymbol(ns.Name.ToString(), 2, ns),
                ClassDeclarationSyntax cls => CreateSymbol(cls.Identifier.Text, 4, cls),
                StructDeclarationSyntax str => CreateSymbol(str.Identifier.Text, 22, str),
                InterfaceDeclarationSyntax iface => CreateSymbol(iface.Identifier.Text, 10, iface),
                EnumDeclarationSyntax en => CreateSymbol(en.Identifier.Text, 9, en),
                MethodDeclarationSyntax method => CreateSymbol(method.Identifier.Text, 5, method),
                PropertyDeclarationSyntax prop => CreateSymbol(prop.Identifier.Text, 6, prop),
                FieldDeclarationSyntax field => CreateSymbol(
                    field.Declaration.Variables.First().Identifier.Text, 7, field),
                ConstructorDeclarationSyntax ctor => CreateSymbol(ctor.Identifier.Text, 5, ctor),
                EnumMemberDeclarationSyntax member => CreateSymbol(member.Identifier.Text, 21, member),
                _ => null
            };

            if (symbol != null)
            {
                CollectSymbols(child, symbol.Children);
                symbols.Add(symbol);
            }
            else
            {
                CollectSymbols(child, symbols);
            }
        }
    }

    private static SymbolDto CreateSymbol(string name, int kind, SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return new SymbolDto
        {
            Name = name,
            Kind = kind,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            StartColumn = lineSpan.StartLinePosition.Character + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            EndColumn = lineSpan.EndLinePosition.Character + 1,
            Children = []
        };
    }

    // ========== Monaco Kind Mapping ==========

    private static int GetMonacoKind(ImmutableArray<string> tags)
    {
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
        return 0;
    }
}

// ========== DTOs ==========

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
    public List<SignatureItemDto> Signatures { get; set; } = [];
    public int ActiveSignature { get; set; }
    public int ActiveParameter { get; set; }
}

public class SignatureItemDto
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

public class HoverDto
{
    public string Content { get; set; } = "";
    public string? Documentation { get; set; }
}

public class LocationDto
{
    public string FileName { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
}

public class CodeActionDto
{
    public string Title { get; set; } = "";
    public int Index { get; set; }
}

public class CodeEditDto
{
    public string NewContent { get; set; } = "";
}

public class TextEditDto
{
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public string Text { get; set; } = "";
}

public class RenameResultDto
{
    public List<FileEditDto> Edits { get; set; } = [];
}

public class FileEditDto
{
    public string FileName { get; set; } = "";
    public List<TextEditDto> TextEdits { get; set; } = [];
}

public class SymbolDto
{
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    public int Kind { get; set; }
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public List<SymbolDto> Children { get; set; } = [];
}
