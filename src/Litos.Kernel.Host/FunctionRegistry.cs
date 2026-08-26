using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Litos.Kernel.Host;

public sealed record FunctionInfo(string Name, string ParameterList, string ReturnType, string? DocComment, string BodyText)
{
    /// <summary>e.g. "FindGreatest(int a, int b) -> int" — the shape §4.1's StateDelta trailer prints. Deliberately excludes BodyText, which exists only for change detection.</summary>
    public string Signature => $"{Name}({ParameterList}) -> {ReturnType}";
}

/// <summary>
/// Tracks locally-declared functions across a ScriptSession's whole lifetime, kept alongside
/// ScriptState rather than derived from it — ScriptState.Variables enumerates top-level variable
/// slots only, so a `int Square(int x) => x * x;` declared in one eval is fully callable in the
/// next but invisible to that listing (ReadMe_PTCPersistentKernel.md §4.1). This is a source-level
/// parse of each submission (CSharpSyntaxTree.ParseText, not reflection on ScriptState's compiled
/// output — §4.1's rationale: a stable, public Roslyn API rather than an internal representation).
/// </summary>
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, FunctionInfo> _functions = [];

    public IReadOnlyDictionary<string, FunctionInfo> Functions => _functions;

    /// <summary>
    /// Scans one submission's source for local function declarations, records/overwrites each by
    /// name (a later declaration with the same name overwrites the earlier entry — a redefinition
    /// should show the latest signature, not both), and returns just the names newly added or
    /// changed by this call, for StateDelta's diff (§4.1's "push, don't rely on pull" fix).
    /// </summary>
    public IReadOnlyList<FunctionInfo> ScanAndDiff(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var changed = new List<FunctionInfo>();

        foreach (var node in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            var name = node.Identifier.Text;
            var parameterList = string.Join(", ", node.ParameterList.Parameters.Select(p => p.ToString()));
            var returnType = node.ReturnType.ToString();
            var docComment = ExtractDocComment(node);
            // Body text (expression-bodied arrow or block) is part of change detection, not just
            // the signature — two declarations can share an identical name/params/return type
            // while having a genuinely different implementation (e.g. Square redefined from n*n to
            // n*n*n), which must still count as a change for StateDelta to report, even though the
            // printed Signature itself never includes the body.
            var bodyText = ((SyntaxNode?)node.Body ?? node.ExpressionBody)?.ToString() ?? "";
            var info = new FunctionInfo(name, parameterList, returnType, docComment, bodyText);

            if (_functions.TryGetValue(name, out var existing) && existing == info)
                continue; // Unchanged redeclaration (e.g. re-running the same eval) — not a diff.

            _functions[name] = info;
            changed.Add(info);
        }

        return changed;
    }

    private static string? ExtractDocComment(LocalFunctionStatementSyntax node)
    {
        var trivia = node.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .Select(t => t.ToFullString().Trim())
            .FirstOrDefault();
        return trivia;
    }
}
