using Bever.Model;

namespace Bever.Core;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info
}

public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, SourceSpan Span)
{
    public override string ToString() => $"{Span.Start}: {Severity} {Code}: {Message}";
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

    public bool HasErrors => diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public void Report(string code, DiagnosticSeverity severity, string message, SourceSpan span) =>
        diagnostics.Add(new Diagnostic(code, severity, message, span));
}
