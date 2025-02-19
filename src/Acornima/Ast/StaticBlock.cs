namespace Acornima.Ast;

[VisitableNode(ChildProperties = new[] { nameof(Body) })]
public sealed partial class StaticBlock : BlockStatement, IClassElement
{
    public StaticBlock(in NodeList<Statement> body) : base(NodeType.StaticBlock, body)
    {
    }

    bool IClassElement.Static => true;

    public new StaticBlock UpdateWith(in NodeList<Statement> body)
    {
        return (StaticBlock)base.UpdateWith(body);
    }

    protected override BlockStatement Rewrite(in NodeList<Statement> body)
    {
        return new StaticBlock(body);
    }
}
