using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Acornima.Helpers;

namespace Acornima.Ast;

// AST hierarchy is based on:
// * https://github.com/estree/estree
// * https://github.com/DefinitelyTyped/DefinitelyTyped/tree/master/types/estree

[DebuggerDisplay($"{{{nameof(GetDebuggerDisplay)}(), nq}}")]
public abstract class Node : INode
{
    protected Node(NodeType type)
    {
        Type = type;
    }

    public NodeType Type { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; }

    public ChildNodes ChildNodes => new ChildNodes(this);

    /// <remarks>
    /// Inheritors who extend the AST with custom node types should override this method and provide an actual implementation.
    /// </remarks>
    protected internal virtual IEnumerator<Node>? GetChildNodes() => null;

    internal virtual Node? NextChildNode(ref ChildNodes.Enumerator enumerator) =>
        throw new NotImplementedException($"User-defined node types should override the {nameof(GetChildNodes)} method and provide an actual implementation.");

    public int Start { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _range.Start; }

    public int End { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _range.End; }

    internal Range _range;
    public Range Range { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _range; init => _range = value; }

    internal SourceLocation _location;
    public SourceLocation Location { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _location; init => _location = value; }

    private AdditionalDataSlot _additionalDataSlot;

    //// TODO: allow multiple pieces of user data?

    /// <summary>
    /// Gets or sets the arbitrary, user-defined data object associated with the current <see cref="Node"/>.
    /// </summary>
    /// <remarks>
    /// The operation is not guaranteed to be thread-safe. In case concurrent access or update is possible, the necessary synchronization is caller's responsibility.
    /// </remarks>
    public object? UserData
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _additionalDataSlot.PrimaryData;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _additionalDataSlot.PrimaryData = value;
    }

    protected internal abstract object? Accept(AstVisitor visitor);

    /// <summary>
    /// Dispatches the visitation of the current node to <see cref="AstVisitor.VisitExtension(Node)"/>.
    /// </summary>
    /// <remarks>
    /// When defining custom node types, inheritors can use this method to implement the abstract <see cref="Accept(AstVisitor)"/> method.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected object? AcceptAsExtension(AstVisitor visitor)
    {
        return visitor.VisitExtension(this);
    }

    // TODO
    // private static readonly AstToJavaScriptOptions s_toStringOptions = AstToJavaScriptOptions.Default with { IgnoreExtensions = true };
    // public override string ToString() => this.ToJavaScriptString(KnRJavaScriptTextFormatterOptions.Default, s_toStringOptions);

    private protected virtual string GetDebuggerDisplay()
    {
        return $"/*{Type}*/  {this}";
    }
}
