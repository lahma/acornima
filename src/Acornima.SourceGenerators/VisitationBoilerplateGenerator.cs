using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Acornima.SourceGenerators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Acornima.SourceGenerators;

// Spec for incremental generators: https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md
// How to implement:
// * https://andrewlock.net/exploring-dotnet-6-part-9-source-generator-updates-incremental-generators/
// * https://www.thinktecture.com/en/net/roslyn-source-generators-performance/
// How to debug: https://stackoverflow.com/a/71314452/8656352

/// <summary>
/// Generates a bunch of boilerplate code of the visitation/enumeration logic based on the annotations of 
/// AST nodes (VisitableNodeAttribute) and AST visitors (AutoGeneratedAstVisitorAttribute).
/// <list type="bullet">
///     <item>NextChildNode methods of annotated AST nodes</item>
///     <item>Accept methods of annotated AST nodes</item>
///     <item>UpdateWith methods of annotated AST nodes</item>
///     <item>VisitXXX methods of annotated AST visitors</item>
/// </list>
/// When a method with a matching signature is already declared,
/// the method won't be auto-generated, which allows us to manually handle the special cases.
/// </summary>
[Generator]
public partial class VisitationBoilerplateGenerator : IIncrementalGenerator
{
    private const string NodeTypeName = "Acornima.Ast.Node";
    private const string NodeCSharpTypeName = "Acornima.Ast.Node";

    private const string NodeListOfTTypeName = "Acornima.Ast.NodeList`1";
    private const string NodeListOfTCSharpTypeName = "Acornima.Ast.NodeList<>";

    private const string ChildNodesEnumeratorTypeName = "Acornima.Ast.ChildNodes+Enumerator";
    private const string ChildNodesEnumeratorCSharpTypeName = "Acornima.Ast.ChildNodes.Enumerator";

    private const string AstVisitorTypeName = "Acornima.AstVisitor";
    private const string AstVisitorCSharpTypeName = "Acornima.AstVisitor";

    private const string VisitMethodNamePrefix = "Visit";

    private static readonly IReadOnlyDictionary<string, string> s_wellKnownTypeNames = new Dictionary<string, string>
    {
        [NodeTypeName] = NodeCSharpTypeName,
        [NodeListOfTTypeName] = NodeListOfTCSharpTypeName,
        [ChildNodesEnumeratorTypeName] = ChildNodesEnumeratorCSharpTypeName,
        [AstVisitorTypeName] = AstVisitorCSharpTypeName,
    };

    // IIncrementalGenerator has an Initialize method that is called by the host exactly once,
    // regardless of the number of further compilations that may occur.
    // For instance a host with multiple loaded projects may share the same generator instance across multiple projects,
    // and will only call Initialize a single time for the lifetime of the host.
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var compilationDiagnostics = context.CompilationProvider
            .Select(GetCompilationDiagnostics);

        IncrementalValuesProvider<VisitableNodeInfo> visitableNodeClassInfos = context.SyntaxProvider
            .CreateSyntaxProvider(IsPotentialVisitableNode, GetVisitableNodeInfo)
            .Where(item => item is not null)!;

        IncrementalValuesProvider<AstVisitorInfo> astVisitorInfos = context.SyntaxProvider
            .CreateSyntaxProvider(IsPotentialAstVisitor, GetAstVisitorInfo)
            .Where(item => item is not null)!;

        var combinedSource = compilationDiagnostics
            .Combine(visitableNodeClassInfos.Collect())
            .Combine(astVisitorInfos.Collect());

        context.RegisterSourceOutput(combinedSource, (context, source) => Execute(context, source.Left.Left, source.Left.Right, source.Right));
    }

    private static StructuralEqualityWrapper<Diagnostic[]> GetCompilationDiagnostics(Compilation compilation, CancellationToken cancellationToken)
    {
        return s_wellKnownTypeNames
            .Select(kvp => (type: compilation.GetTypeByMetadataName(kvp.Key), csharpTypeName: kvp.Value))
            .Where(item => item.type is null)
            .Select(item => Diagnostic.Create(Diagnostics.TypeNotFoundError, Location.None, item.csharpTypeName))
            .ToArray();
    }

    private static bool IsPotentialVisitableNode(SyntaxNode node, CancellationToken cancellationToken)
    {
        return node is ClassDeclarationSyntax classDeclarationSyntax
            && classDeclarationSyntax.AttributeLists.Count > 0;
    }

    private static VisitableNodeInfo? GetVisitableNodeInfo(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        // 1. Discover classes annotated with the expected attribute

        var classDeclarationSyntax = (ClassDeclarationSyntax)context.Node;
        var classType = context.SemanticModel.GetDeclaredSymbol(classDeclarationSyntax, cancellationToken);

        INamedTypeSymbol? nodeType, nodeListType, childNodesEnumeratorType, astVisitorType;
        AttributeData? attribute;

        if (classType is null
            || (attribute = classType.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.Name == "VisitableNodeAttribute")) is null
            || attribute.AttributeClass!.ContainingType is not null
            || attribute.AttributeClass.ContainingNamespace.ToString() != "Acornima.Ast"
            // Class may be split into multiple files but should be analyzed only once.
            || classDeclarationSyntax != classType.DeclaringSyntaxReferences.First().GetSyntax(cancellationToken)
            || (nodeType = context.SemanticModel.Compilation.GetTypeByMetadataName(NodeTypeName)) is null
            || (nodeListType = context.SemanticModel.Compilation.GetTypeByMetadataName(NodeListOfTTypeName)?.ConstructUnboundGenericType()) is null
            || (childNodesEnumeratorType = context.SemanticModel.Compilation.GetTypeByMetadataName(ChildNodesEnumeratorTypeName)) is null
            || (astVisitorType = context.SemanticModel.Compilation.GetTypeByMetadataName(AstVisitorTypeName)) is null
            || CSharpTypeName.From(classType) is not { } className)
        {
            return null;
        }

        if (// Class must be non-nested.
            classType.ContainingType is not null
            // Class must be non-generic.
            || classType.IsGenericType
            // Class must be partial.
            || !classDeclarationSyntax.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword))
            // Class must inherit from Node.
            || !classType.InheritsFrom(nodeType))
        {
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;
            var diagnostic = Diagnostic.Create(Diagnostics.InvalidVisitableNodeAttributeUsageWarning, location,
                classType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                nodeType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            return new VisitableNodeInfo(className) { Diagnostics = new[] { diagnostic } };
        }

        // 2. Extract information from the attribute

        var namedArgument = attribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == "VisitorType");
        var visitorType = namedArgument.Key is not null ? (ITypeSymbol?)namedArgument.Value.Value : null;
        CSharpTypeName? visitorTypeName;
        if (visitorType is null)
        {
            visitorTypeName = null;
        }
        else if ((visitorTypeName = CSharpTypeName.From(visitorType)) is null)
        {
            return null;
        }

        namedArgument = attribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == "ChildProperties");
        var childPropertyNames = namedArgument.Key is not null ? namedArgument.Value.Values.Select(value => (string)value.Value!).ToArray() : Array.Empty<string>();

        namedArgument = attribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == "SealOverrideMethods");
        var sealOverrideMethods = namedArgument.Key is not null ? (bool)namedArgument.Value.Value! : false;

        // 3. Collect information about child properties

        var (childProperties, childPropertyInfos) = childPropertyNames.Length > 0
            ? (new IPropertySymbol[childPropertyNames.Length], new VisitableNodeChildPropertyInfo[childPropertyNames.Length])
            : (Array.Empty<IPropertySymbol>(), Array.Empty<VisitableNodeChildPropertyInfo>());
        List<Diagnostic>? diagnostics = null;

        ISymbol? member;
        for (var i = 0; i < childPropertyNames.Length; i++)
        {
            var propertyName = childPropertyNames[i];
            member = classType.GetBaseTypes()
                .Prepend(classType)
                .SelectMany(type => type.GetMembers(propertyName))
                .FirstOrDefault();

            if (member is null
                || member is not IPropertySymbol property)
            {
                var diagnostic = Diagnostic.Create(Diagnostics.PropertyNotFoundError, classDeclarationSyntax.GetLocation(),
                    classType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    propertyName);
                (diagnostics ??= new List<Diagnostic>(capacity: 1)).Add(diagnostic);
                continue;
            }

            static ITypeSymbol? GetChildNodeType(IPropertySymbol property, INamedTypeSymbol nodeType, INamedTypeSymbol nodeListType, out bool isNodeList)
            {
                if (property.Type is INamedTypeSymbol { IsGenericType: true } namedType
                    && SymbolEqualityComparer.Default.Equals(namedType.ConstructUnboundGenericType(), nodeListType))
                {
                    isNodeList = true;
                    return property.ReturnsByRefReadonly ? namedType.TypeArguments[0] : null;
                }

                isNodeList = false;
                return property.RefKind == RefKind.None && property.Type.InheritsFromOrIsSameAs(nodeType) ? property.Type : null;
            }

            ITypeSymbol? childNodeType;
            if (property.GetMethod is null
                || (childNodeType = GetChildNodeType(property, nodeType, nodeListType, out var isNodeList)) is null)
            {
                var location = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;
                var diagnostic = Diagnostic.Create(Diagnostics.InvalidVisitableNodeChildNodePropertyError, location,
                    classType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    propertyName,
                    nodeType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    nodeListType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                (diagnostics ??= new List<Diagnostic>(capacity: 1)).Add(diagnostic);
                continue;
            }

            if (CSharpTypeName.From(property.Type) is not { } propertyTypeName)
            {
                return null;
            }

            childPropertyInfos[i] = new VisitableNodeChildPropertyInfo(property.Name, propertyTypeName)
            {
                IsOptional = childNodeType.NullableAnnotation == NullableAnnotation.Annotated,
                IsNodeList = isNodeList,
                IsRefReadonly = property.ReturnsByRefReadonly
            };
            childProperties[i] = property;
        }

        if (diagnostics is not null)
        {
            return new VisitableNodeInfo(className) { Diagnostics = diagnostics.ToArray() };
        }

        // 4. Determine which methods to generate

        // NOTE: SymbolEqualityComparer.Default.Equals doesn't care whether the compared types are by-ref or not.

        var generateNextChildNodeMethod = !classType.GetMembers("NextChildNode")
            .OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == 1
                && method.Parameters[0] is { RefKind: RefKind.Ref } param
                && SymbolEqualityComparer.Default.Equals(param.Type, childNodesEnumeratorType))
            .Any();

        visitorType ??= astVisitorType;
        var generateAcceptMethod = !classType.GetMembers("Accept")
            .OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == 1
                && method.Parameters[0] is { RefKind: RefKind.None } param
                && SymbolEqualityComparer.Default.Equals(param.Type, visitorType))
            .Any();

        static bool IsMatchingUpdateWithMethodSignature(IMethodSymbol method, IPropertySymbol[] childProperties)
        {
            return method.Parameters.Length == childProperties.Length
                && method.Parameters
                    .Zip(childProperties, (param, property) =>
                        param.RefKind == (property.ReturnsByRefReadonly ? RefKind.In : RefKind.None)
                        && SymbolEqualityComparer.Default.Equals(param.Type, property.Type))
                    .All(isMatchingParam => isMatchingParam);
        }

        var generateUpdateWithMethod = childProperties.Length > 0
            && !classType.GetMembers("UpdateWith")
                .OfType<IMethodSymbol>()
                .Where(method => IsMatchingUpdateWithMethodSignature(method, childProperties))
                .Any();

        return new VisitableNodeInfo(className)
        {
            VisitorTypeName = visitorTypeName,
            ChildPropertyInfos = childPropertyInfos,
            SealOverrideMethods = sealOverrideMethods,
            GenerateNextChildNodeMethod = generateNextChildNodeMethod,
            GenerateAcceptMethod = generateAcceptMethod,
            GenerateUpdateWithMethod = generateUpdateWithMethod,
        };
    }

    private static bool IsPotentialAstVisitor(SyntaxNode node, CancellationToken cancellationToken)
    {
        return node is ClassDeclarationSyntax classDeclarationSyntax
            && classDeclarationSyntax.AttributeLists.Count > 0;
    }

    private static AstVisitorInfo? GetAstVisitorInfo(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        // 1. Discover classes annotated with the expected attribute

        var classDeclarationSyntax = (ClassDeclarationSyntax)context.Node;
        var classType = context.SemanticModel.GetDeclaredSymbol(classDeclarationSyntax, cancellationToken);

        INamedTypeSymbol? nodeType, astVisitorType;
        AttributeData? attribute;
        VisitorKind visitorKind;

        static VisitorKind GetVisitorKind(INamedTypeSymbol type)
        {
            if (type.Name.EndsWith("Visitor", StringComparison.Ordinal))
            {
                return VisitorKind.Visitor;
            }
            else if (type.Name.EndsWith("Rewriter", StringComparison.Ordinal))
            {
                return VisitorKind.Rewriter;
            }

            return VisitorKind.Unknown;
        }

        if (classType is null
            || (attribute = classType.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.Name == "AutoGeneratedAstVisitorAttribute")) is null
            || attribute.AttributeClass!.ContainingType is not null
            || attribute.AttributeClass.ContainingNamespace.ToString() != "Acornima"
            // Class may be split into multiple files but should be analyzed only once.
            || classDeclarationSyntax != classType.DeclaringSyntaxReferences.First().GetSyntax(cancellationToken)
            || (visitorKind = GetVisitorKind(classType)) == VisitorKind.Unknown
            || (nodeType = context.SemanticModel.Compilation.GetTypeByMetadataName(NodeTypeName)) is null
            || (astVisitorType = context.SemanticModel.Compilation.GetTypeByMetadataName(AstVisitorTypeName)) is null
            || CSharpTypeName.From(classType) is not { } className)
        {
            return null;
        }

        if (// Class must be non-nested.
            classType.ContainingType is not null
            // Class must be partial.
            || !classDeclarationSyntax.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword))
            // Class must inherit from Node.
            || !classType.InheritsFromOrIsSameAs(astVisitorType))
        {
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;
            var diagnostic = Diagnostic.Create(Diagnostics.InvalidAutoGeneratedAstVisitorAttributeUsageWarning, location,
                classType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                astVisitorType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            return new AstVisitorInfo(className) { Diagnostics = new[] { diagnostic } };
        }

        // 2. Extract information from the attribute

        var namedArgument = attribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == "VisitorType");
        var visitorType = namedArgument.Key is not null ? (ITypeSymbol?)namedArgument.Value.Value : null;
        CSharpTypeName? visitorTypeName;
        if (visitorType is null)
        {
            visitorTypeName = null;
        }
        else if ((visitorTypeName = CSharpTypeName.From(visitorType)) is null)
        {
            return null;
        }

        namedArgument = attribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == "TargetVisitorFieldName");
        var targetVisitorFieldName = namedArgument.Key is not null ? (string?)namedArgument.Value.Value : null;

        namedArgument = attribute.NamedArguments.FirstOrDefault(kvp => kvp.Key == "BaseVisitorFieldName");
        var baseVisitorFieldName = namedArgument.Key is not null ? (string?)namedArgument.Value.Value : null;

        // 3. Find manually defined visit methods

        var definedVisitMethods = classType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method =>
                method.Name.Length > VisitMethodNamePrefix.Length
                && method.Parameters.Length == 1
                && method.Parameters[0].Type.InheritsFrom(nodeType)
                && VisitMethodNamePrefix + method.Parameters[0].Type.Name == method.Name)
            .Select(method => method.Name)
            .ToArray();

        return new AstVisitorInfo(className)
        {
            VisitorTypeName = visitorTypeName,
            Kind = visitorKind,
            DefinedVisitMethods = definedVisitMethods,
            TargetVisitorFieldName = targetVisitorFieldName,
            BaseVisitorFieldName = baseVisitorFieldName,
        };
    }

    private static void Execute(SourceProductionContext context, StructuralEqualityWrapper<Diagnostic[]> compilationDiagnostics,
        ImmutableArray<VisitableNodeInfo> visitableNodeInfos, ImmutableArray<AstVisitorInfo> astVisitorInfos)
    {
        var diagnostics = compilationDiagnostics.Target
            .Concat(visitableNodeInfos.SelectMany(nodeInfo => nodeInfo.Diagnostics))
            .Concat(astVisitorInfos.SelectMany(nodeInfo => nodeInfo.Diagnostics))
            .ToArray();

        if (diagnostics.Length > 0)
        {
            var hasError = false;

            foreach (var diagnostic in diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
                hasError = hasError || diagnostic.Severity >= DiagnosticSeverity.Error;
            }

            if (hasError)
            {
                return;
            }
        }

        var nodeGroupsByNamespace = visitableNodeInfos
            .Where(nodeInfo => !nodeInfo.Diagnostics.Any(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning))
            .GroupBy(nodeInfo => nodeInfo.ClassName.Namespace);

        var sb = new SourceBuilder();

        foreach (var nodesByNamespace in nodeGroupsByNamespace)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var @namespace = nodesByNamespace.Key;

            GenerateVisitableNodeClasses(sb, @namespace, nodesByNamespace, context.CancellationToken);

            context.AddSource($"{(@namespace is not null ? @namespace + "." : string.Empty)}VisitableNodes.g.cs", sb.ToString());
            sb.Reset();
        }

        GenerateChildNodesEnumeratorHelpers(sb, visitableNodeInfos, context.CancellationToken);

        context.AddSource($"ChildNodes.Helpers.g.cs", sb.ToString());
        sb.Reset();

        var nodeLookupByVisitorType = visitableNodeInfos
            .ToLookup(nodeInfo => nodeInfo.VisitorTypeName?.ToString() ?? AstVisitorCSharpTypeName);

        foreach (var astVisitorInfo in astVisitorInfos)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var visitorTypeName = astVisitorInfo.VisitorTypeName;
            var nodesByVisitorType = nodeLookupByVisitorType[visitorTypeName?.ToString() ?? AstVisitorCSharpTypeName];

            string astVisitorFileName;

            if (astVisitorInfo is { Kind: VisitorKind.Visitor, VisitorTypeName.TypeKind: TypeKind.Interface })
            {
                if (astVisitorInfo.VisitorTypeName.BareName.Container is not null)
                {
                    throw new NotImplementedException("Support for nested classes is not implemented yet.");
                }

                GenerateAstVisitorInterface(sb, astVisitorInfo, nodesByVisitorType, context.CancellationToken);

                astVisitorFileName = visitorTypeName is { BareName.IsGeneric: true }
                    ? visitorTypeName.ToNonGeneric().ToString() + "`"
                        + visitorTypeName.BareName.GenericArguments.Length.ToString(CultureInfo.InvariantCulture)
                    : visitorTypeName!.ToString();

                context.AddSource($"{astVisitorFileName}.g.cs", sb.ToString());
                sb.Reset();
            }

            if (astVisitorInfo.ClassName.BareName.Container is not null)
            {
                throw new NotImplementedException("Support for nested classes is not implemented yet.");
            }

            GenerateAstVisitorClass(sb, astVisitorInfo, nodesByVisitorType, context.CancellationToken);

            astVisitorFileName = astVisitorInfo.ClassName is { BareName.IsGeneric: true }
                ? astVisitorInfo.ClassName.ToNonGeneric().ToString() + "`"
                    + astVisitorInfo.ClassName.BareName.GenericArguments.Length.ToString(CultureInfo.InvariantCulture)
                : astVisitorInfo.ClassName.ToString();

            context.AddSource($"{astVisitorFileName}.g.cs", sb.ToString());
            sb.Reset();
        }
    }
}

internal sealed record class VisitableNodeInfo
{
    public VisitableNodeInfo(CSharpTypeName className)
    {
        ClassName = className;

        // VariableName = CodeGenerationHelper.MakeValidVariableName(CodeGenerationHelper.ToCamelCase(className.TypeName));
        VariableName = "node";
    }

    public CSharpTypeName ClassName { get; }

    public string VariableName { get; }

    public CSharpTypeName? VisitorTypeName { get; init; }

    private StructuralEqualityWrapper<VisitableNodeChildPropertyInfo[]> _childProperties = Array.Empty<VisitableNodeChildPropertyInfo>();
    public VisitableNodeChildPropertyInfo[] ChildPropertyInfos { get => _childProperties.Target; init => _childProperties = value; }

    public bool SealOverrideMethods { get; init; }

    public bool GenerateNextChildNodeMethod { get; init; }
    public bool GenerateAcceptMethod { get; init; }
    public bool GenerateUpdateWithMethod { get; init; }

    private StructuralEqualityWrapper<Diagnostic[]> _diagnostics = Array.Empty<Diagnostic>();
    public Diagnostic[] Diagnostics { get => _diagnostics.Target; init => _diagnostics = value; }
}

internal sealed record class VisitableNodeChildPropertyInfo : IChildNodesEnumeratorHelperParamInfo
{
    public VisitableNodeChildPropertyInfo(string propertyName, CSharpTypeName propertyTypeName)
    {
        PropertyName = propertyName;
        PropertyTypeName = propertyTypeName;

        VariableName = CodeGenerationHelper.MakeValidVariableName(CodeGenerationHelper.ToCamelCase(propertyName));
    }

    public string PropertyName { get; }

    public CSharpTypeName PropertyTypeName { get; }

    public bool IsOptional { get; init; }

    public string VariableName { get; }

    public bool IsNodeList { get; init; }

    public bool IsRefReadonly { get; init; }
}

internal interface IChildNodesEnumeratorHelperParamInfo
{
    bool IsOptional { get; }
    bool IsNodeList { get; }
}

internal enum VisitorKind
{
    Unknown,
    Visitor,
    Rewriter,
}

internal sealed record class AstVisitorInfo
{
    public AstVisitorInfo(CSharpTypeName className)
    {
        ClassName = className;
    }

    public CSharpTypeName ClassName { get; }

    public CSharpTypeName? VisitorTypeName { get; init; }

    public VisitorKind Kind { get; init; }

    public string? TargetVisitorFieldName { get; init; }
    public string? BaseVisitorFieldName { get; init; }

    private StructuralEqualityWrapper<string[]> _definedVisitMethods = Array.Empty<string>();
    public string[] DefinedVisitMethods { get => _definedVisitMethods.Target; init => _definedVisitMethods = value; }

    private StructuralEqualityWrapper<Diagnostic[]> _diagnostics = Array.Empty<Diagnostic>();
    public Diagnostic[] Diagnostics { get => _diagnostics.Target; init => _diagnostics = value; }
}
