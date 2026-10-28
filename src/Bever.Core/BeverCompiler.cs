using Bever.Model;

namespace Bever.Core;

public sealed class BeverCompiler
{
    public CompilationResult Compile(IEnumerable<SourceText> sources)
    {
        var diagnostics = new DiagnosticBag();
        var parser = new BeverParser(diagnostics);
        var syntaxTrees = sources.Select(parser.Parse).ToList();
        var model = new SemanticModelBuilder(diagnostics).Build(syntaxTrees);
        if (diagnostics.HasErrors)
        {
            model = BeverModel.CreateEmpty();
        }

        return new CompilationResult(model, syntaxTrees, diagnostics.Diagnostics);
    }
}

public sealed record CompilationResult(
    BeverModel Model,
    IReadOnlyList<CompilationUnitSyntax> SyntaxTrees,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}
