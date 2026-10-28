using Bever.Model;

namespace Bever.Core;

public abstract record SyntaxNode(SourceSpan Span);

public sealed record CompilationUnitSyntax(IReadOnlyList<MemberSyntax> Members, SourceSpan Span) : SyntaxNode(Span);

public abstract record MemberSyntax(SourceSpan Span) : SyntaxNode(Span);

public sealed record EntityDeclarationSyntax(string Name, IReadOnlyList<EntityMemberSyntax> Members, SourceSpan Span, bool Exported = false) : MemberSyntax(Span);

public sealed record ImportDeclarationSyntax(IReadOnlyList<string> Names, string Path, SourceSpan Span) : MemberSyntax(Span);

public sealed record IdentityDeclarationSyntax(IReadOnlyList<IdentityMemberSyntax> Members, SourceSpan Span) : MemberSyntax(Span);

public sealed record IdentityMemberSyntax(string Text, SourceSpan Span) : SyntaxNode(Span);

public sealed record RoleDeclarationSyntax(string Name, SourceSpan Span) : MemberSyntax(Span);

public sealed record ActionDeclarationSyntax(string Text, IReadOnlyList<ActionMemberSyntax> Members, SourceSpan Span) : MemberSyntax(Span);

public abstract record EntityMemberSyntax(SourceSpan Span) : SyntaxNode(Span);

public sealed record FieldDeclarationSyntax(string Name, string? TypeText, SourceSpan Span) : EntityMemberSyntax(Span);

public sealed record RuleDeclarationSyntax(string Text, SourceSpan Span) : EntityMemberSyntax(Span);

public sealed record PresentDeclarationSyntax(
    IReadOnlyList<PresentationMemberSyntax> Members,
    SourceSpan Span) : EntityMemberSyntax(Span);

public sealed record PresentationMemberSyntax(
    string Text,
    IReadOnlyList<PresentationMemberSyntax> Members,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record UnknownEntityMemberSyntax(string Text, SourceSpan Span) : EntityMemberSyntax(Span);

public sealed record UnknownTopLevelMemberSyntax(string Text, SourceSpan Span) : MemberSyntax(Span);

public abstract record ActionMemberSyntax(SourceSpan Span) : SyntaxNode(Span);

public sealed record AuthorizationClauseSyntax(string Text, SourceSpan Span) : ActionMemberSyntax(Span);

public sealed record OrderByClauseSyntax(string Text, SourceSpan Span) : ActionMemberSyntax(Span);

public sealed record SearchByClauseSyntax(string Text, SourceSpan Span) : ActionMemberSyntax(Span);

public sealed record UnknownActionMemberSyntax(string Text, SourceSpan Span) : ActionMemberSyntax(Span);
