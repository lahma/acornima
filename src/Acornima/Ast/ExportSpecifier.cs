using System.Runtime.CompilerServices;

namespace Acornima.Ast;

[VisitableNode(ChildProperties = new[] { nameof(Local), nameof(Exported) })]
public sealed partial class ExportSpecifier : ModuleSpecifier
{
    public ExportSpecifier(Expression local, Expression exported) : base(NodeType.ExportSpecifier)
    {
        Local = local;
        Exported = exported;
    }

    /// <remarks>
    /// <see cref="Identifier"/> | <see cref="Literal"/> (string)
    /// </remarks>
    public new Expression Local { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; }
    protected override Expression GetLocal() => Local;

    /// <remarks>
    /// <see cref="Identifier"/> | <see cref="Literal"/> (string)
    /// </remarks>
    public Expression Exported { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; }

    internal override Node? NextChildNode(ref ChildNodes.Enumerator enumerator) => enumerator.MoveNextExportSpecifier(Local, Exported);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ExportSpecifier Rewrite(Expression local, Expression exported)
    {
        return new ExportSpecifier(local, exported);
    }
}
