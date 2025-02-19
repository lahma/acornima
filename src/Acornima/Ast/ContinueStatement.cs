﻿using System.Runtime.CompilerServices;

namespace Acornima.Ast;

[VisitableNode(ChildProperties = new[] { nameof(Label) })]
public sealed partial class ContinueStatement : Statement
{
    public ContinueStatement(Identifier? label) : base(NodeType.ContinueStatement)
    {
        Label = label;
    }

    public Identifier? Label { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ContinueStatement Rewrite(Identifier? label)
    {
        return new ContinueStatement(label);
    }
}
