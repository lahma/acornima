﻿using System.Runtime.CompilerServices;

namespace Acornima.Ast;

[VisitableNode(ChildProperties = new[] { nameof(Local) })]
public sealed partial class ImportDefaultSpecifier : ImportDeclarationSpecifier
{
    public ImportDefaultSpecifier(Identifier local) : base(local, NodeType.ImportDefaultSpecifier)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ImportDefaultSpecifier Rewrite(Identifier local)
    {
        return new ImportDefaultSpecifier(local);
    }
}
