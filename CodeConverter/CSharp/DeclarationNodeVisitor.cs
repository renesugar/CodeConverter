﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using StringComparer = System.StringComparer;
using SyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VBasic = Microsoft.CodeAnalysis.VisualBasic;
using SyntaxToken = Microsoft.CodeAnalysis.SyntaxToken;
using Microsoft.CodeAnalysis.VisualBasic;
using ICSharpCode.CodeConverter.Util.FromRoslyn;

namespace ICSharpCode.CodeConverter.CSharp
{
    /// <summary>
    /// Declaration nodes, and nodes only used directly in that declaration (i.e. never within an expression)
    /// e.g. Class, Enum, TypeConstraint
    /// </summary>
    /// <remarks>The split between this and the <see cref="ExpressionNodeVisitor"/> is purely organizational and serves no real runtime purpose.</remarks>
    internal class DeclarationNodeVisitor : VBasic.VisualBasicSyntaxVisitor<Task<CSharpSyntaxNode>>
    {
        private static readonly Type DllImportType = typeof(DllImportAttribute);
        private static readonly Type CharSetType = typeof(CharSet);
        private static readonly SyntaxToken SemicolonToken = SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SemicolonToken);
        private static readonly TypeSyntax VarType = SyntaxFactory.ParseTypeName("var");
        private readonly CSharpCompilation _csCompilation;
        private readonly SyntaxGenerator _csSyntaxGenerator;
        private readonly Compilation _compilation;
        private readonly SemanticModel _semanticModel;
        private readonly MethodsWithHandles _methodsWithHandles = new MethodsWithHandles();
        private readonly Dictionary<VBSyntax.StatementSyntax, MemberDeclarationSyntax[]> _additionalDeclarations = new Dictionary<VBSyntax.StatementSyntax, MemberDeclarationSyntax[]>();
        private readonly AdditionalInitializers _additionalInitializers;
        private readonly AdditionalLocals _additionalLocals = new AdditionalLocals();
        private uint _failedMemberConversionMarkerCount;
        private readonly HashSet<string> _extraUsingDirectives = new HashSet<string>();
        private readonly VisualBasicEqualityComparison _visualBasicEqualityComparison;
        private HashSet<string> _accessedThroughMyClass;
        public CommentConvertingVisitorWrapper TriviaConvertingDeclarationVisitor { get; }
        private readonly CommentConvertingVisitorWrapper _triviaConvertingExpressionVisitor;


        private string _topAncestorNamespace;

        private CommonConversions CommonConversions { get; }
        private Func<VisualBasicSyntaxNode, bool, IdentifierNameSyntax, Task<VisualBasicSyntaxVisitor<Task<SyntaxList<StatementSyntax>>>>> _createMethodBodyVisitorAsync { get; }

        public DeclarationNodeVisitor(Document document, Compilation compilation, SemanticModel semanticModel,
            CSharpCompilation csCompilation, SyntaxGenerator csSyntaxGenerator)
        {
            _compilation = compilation;
            _semanticModel = semanticModel;
            _csCompilation = csCompilation;
            _csSyntaxGenerator = csSyntaxGenerator;
            _visualBasicEqualityComparison = new VisualBasicEqualityComparison(_semanticModel, _extraUsingDirectives);
            TriviaConvertingDeclarationVisitor = new CommentConvertingVisitorWrapper(this, _semanticModel.SyntaxTree);
            var expressionEvaluator = new ExpressionEvaluator(semanticModel, _visualBasicEqualityComparison);
            var typeConversionAnalyzer = new TypeConversionAnalyzer(semanticModel, csCompilation, _extraUsingDirectives, _csSyntaxGenerator, expressionEvaluator);
            CommonConversions = new CommonConversions(document, semanticModel, typeConversionAnalyzer, csSyntaxGenerator, csCompilation);
            _additionalInitializers = new AdditionalInitializers();
            var expressionNodeVisitor = new ExpressionNodeVisitor(semanticModel, _visualBasicEqualityComparison, _additionalLocals, csCompilation, _methodsWithHandles, CommonConversions, _extraUsingDirectives);
            _triviaConvertingExpressionVisitor = expressionNodeVisitor.TriviaConvertingExpressionVisitor;
            _createMethodBodyVisitorAsync = expressionNodeVisitor.CreateMethodBodyVisitor;
            CommonConversions.TriviaConvertingExpressionVisitor = _triviaConvertingExpressionVisitor;
        }

        public async Task<VBasic.VisualBasicSyntaxVisitor<Task<SyntaxList<StatementSyntax>>>> CreateMethodBodyVisitor(VBasic.VisualBasicSyntaxNode node, bool isIterator = false, IdentifierNameSyntax csReturnVariable = null)
        {
            return await _createMethodBodyVisitorAsync(node, isIterator, csReturnVariable);
        }

        public override async Task<CSharpSyntaxNode> DefaultVisit(SyntaxNode node)
        {
            throw new NotImplementedException($"Conversion for {VBasic.VisualBasicExtensions.Kind(node)} not implemented, please report this issue")
                .WithNodeInformation(node);
        }

        public override async Task<CSharpSyntaxNode> VisitCompilationUnit(VBSyntax.CompilationUnitSyntax node)
        {
            var options = (VBasic.VisualBasicCompilationOptions)_semanticModel.Compilation.Options;
            var importsClauses = options.GlobalImports.Select(gi => gi.Clause).Concat(node.Imports.SelectMany(imp => imp.ImportsClauses)).ToList();

            var optionCompareText = node.Options.Any(x => x.NameKeyword.ValueText.Equals("Compare", StringComparison.OrdinalIgnoreCase) &&
                                                       x.ValueKeyword.ValueText.Equals("Text", StringComparison.OrdinalIgnoreCase));
            _topAncestorNamespace = node.Members.Any(m => !IsNamespaceDeclaration(m)) ? options.RootNamespace : null;
            _visualBasicEqualityComparison.OptionCompareTextCaseInsensitive = optionCompareText;

            var attributes = SyntaxFactory.List(await node.Attributes.SelectMany(a => a.AttributeLists).SelectManyAsync(CommonConversions.ConvertAttribute));
            var sourceAndConverted = await node.Members
                .Where(m => !(m is VBSyntax.OptionStatementSyntax)) //TODO Record values for use in conversion
                .SelectAsync(async m => (Source: m, Converted: await ConvertMember(m)));


            var convertedMembers = string.IsNullOrEmpty(options.RootNamespace)
                ? sourceAndConverted.Select(sd => sd.Converted)
                : PrependRootNamespace(sourceAndConverted, SyntaxFactory.IdentifierName(options.RootNamespace));

            var usings = await importsClauses
                .SelectAsync(async c => (UsingDirectiveSyntax)await c.AcceptAsync(TriviaConvertingDeclarationVisitor));
            var usingDirectiveSyntax = usings
                .Concat(_extraUsingDirectives.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u))))
                .OrderByDescending(IsSystemUsing).ThenBy(u => u.Name.ToString().Replace("global::", "")).ThenByDescending(HasSourceMapAnnotations)
                .GroupBy(u => (Name: u.Name.ToString(), Alias: u.Alias))
                .Select(g => g.First());

            return SyntaxFactory.CompilationUnit(
                SyntaxFactory.List<ExternAliasDirectiveSyntax>(),
                SyntaxFactory.List(usingDirectiveSyntax),
                attributes,
                SyntaxFactory.List(convertedMembers)
            );
        }

        private static bool IsSystemUsing(UsingDirectiveSyntax u)
        {
            return u.Name.ToString().StartsWith("System") || u.Name.ToString().StartsWith("global::System");
        }

        private static bool HasSourceMapAnnotations(UsingDirectiveSyntax c)
        {
            return c.HasAnnotations(new[] { AnnotationConstants.SourceStartLineAnnotationKind, AnnotationConstants.SourceEndLineAnnotationKind });
        }

        private IReadOnlyCollection<MemberDeclarationSyntax> PrependRootNamespace(
            IReadOnlyCollection<(VBSyntax.StatementSyntax VbNode, MemberDeclarationSyntax CsNode)> membersConversions,
            IdentifierNameSyntax rootNamespaceIdentifier)
        {

            if (_topAncestorNamespace != null) {
                var csMembers = membersConversions.ToLookup(c => ShouldBeNestedInRootNamespace(c.VbNode, rootNamespaceIdentifier.Identifier.Text), c => c.CsNode);
                var nestedMembers = csMembers[true].Select<MemberDeclarationSyntax, SyntaxNode>(x => x);
                var newNamespaceDecl = (MemberDeclarationSyntax) _csSyntaxGenerator.NamespaceDeclaration(rootNamespaceIdentifier.Identifier.Text, nestedMembers);
                return csMembers[false].Concat(new[] { newNamespaceDecl }).ToArray();
            }
            return membersConversions.Select(n => n.CsNode).ToArray();
        }

        private bool ShouldBeNestedInRootNamespace(VBSyntax.StatementSyntax vbStatement, string rootNamespace)
        {
            return _semanticModel.GetDeclaredSymbol(vbStatement)?.ToDisplayString().StartsWith(rootNamespace) == true;
        }

        private bool IsNamespaceDeclaration(VBSyntax.StatementSyntax m)
        {
            return m is VBSyntax.NamespaceBlockSyntax;
        }

        public override async Task<CSharpSyntaxNode> VisitSimpleImportsClause(VBSyntax.SimpleImportsClauseSyntax node)
        {
            var nameEqualsSyntax = node.Alias == null ? null
                : SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName(CommonConversions.ConvertIdentifier(node.Alias.Identifier)));
            var usingDirective = SyntaxFactory.UsingDirective(nameEqualsSyntax, (NameSyntax) await node.Name.AcceptAsync(_triviaConvertingExpressionVisitor));
            return usingDirective;
        }

        public override async Task<CSharpSyntaxNode> VisitNamespaceBlock(VBSyntax.NamespaceBlockSyntax node)
        {
            var members = (await node.Members.SelectAsync(ConvertMember)).Where(m => m != null);
            var sym = ModelExtensions.GetDeclaredSymbol(_semanticModel, node);
            string namespaceToDeclare = await WithDeclarationCasing(node, sym);
            var parentNamespaceSyntax = node.GetAncestor<VBSyntax.NamespaceBlockSyntax>();
            var parentNamespaceDecl = parentNamespaceSyntax != null ? ModelExtensions.GetDeclaredSymbol(_semanticModel, parentNamespaceSyntax) : null;
            var parentNamespaceFullName = parentNamespaceDecl?.ToDisplayString() ?? _topAncestorNamespace;
            if (parentNamespaceFullName != null && namespaceToDeclare.StartsWith(parentNamespaceFullName + "."))
                namespaceToDeclare = namespaceToDeclare.Substring(parentNamespaceFullName.Length + 1);

            var cSharpSyntaxNode = (CSharpSyntaxNode) _csSyntaxGenerator.NamespaceDeclaration(namespaceToDeclare, SyntaxFactory.List(members));
            return cSharpSyntaxNode;
        }

        /// <summary>
        /// Semantic model merges the symbols, but the compiled form retains multiple namespaces, which (when referenced from C#) need to keep the correct casing.
        /// <seealso cref="CommonConversions.WithDeclarationCasing(TypeSyntax, ITypeSymbol)"/>
        /// <seealso cref="CommonConversions.WithDeclarationCasing(SyntaxToken, ISymbol, string)"/>
        /// </summary>
        private async Task<string> WithDeclarationCasing(VBSyntax.NamespaceBlockSyntax node, ISymbol sym)
        {
            var sourceName = (await node.NamespaceStatement.Name.AcceptAsync(_triviaConvertingExpressionVisitor)).ToString();
            var namespaceToDeclare = sym?.ToDisplayString() ?? sourceName;
            int lastIndex = namespaceToDeclare.LastIndexOf(sourceName, StringComparison.OrdinalIgnoreCase);
            if (lastIndex >= 0 && lastIndex + sourceName.Length == namespaceToDeclare.Length)
            {
                namespaceToDeclare = namespaceToDeclare.Substring(0, lastIndex) + sourceName;
            }

            return namespaceToDeclare;
        }

        #region Namespace Members

        private async Task<IEnumerable<MemberDeclarationSyntax>> ConvertMembers(VBSyntax.TypeBlockSyntax parentType)
        {
            var members = parentType.Members;

            //TODO: Store these per-type so nested classes aren't affected
            _methodsWithHandles.Initialize(GetMethodWithHandles(parentType));
            _additionalInitializers.AdditionalInstanceInitializers.Clear();
            _additionalInitializers.AdditionalInstanceInitializers.Clear();

            if (_methodsWithHandles.Any()) _extraUsingDirectives.Add("System.Runtime.CompilerServices");//For MethodImplOptions.Synchronized

            var directlyConvertedMembers = await GetDirectlyConvertMembers();

            var namedTypeSymbol = _semanticModel.GetDeclaredSymbol(parentType);
            bool shouldAddTypeWideInitToThisPart = ShouldAddTypeWideInitToThisPart(parentType, namedTypeSymbol);
            var requiresInitializeComponent = namedTypeSymbol.IsDesignerGeneratedTypeWithInitializeComponent(_compilation);

            if (shouldAddTypeWideInitToThisPart) {
                if (requiresInitializeComponent) {
                    // Constructor event handlers not required since they'll be inside InitializeComponent
                    directlyConvertedMembers = directlyConvertedMembers.Concat(_methodsWithHandles.CreateDelegatingMethodsRequiredByInitializeComponent());
                } else {
                    _additionalInitializers.AdditionalInstanceInitializers.AddRange(_methodsWithHandles.GetConstructorEventHandlers());
                }
            }

            return _additionalInitializers.WithAdditionalInitializers(namedTypeSymbol, directlyConvertedMembers.ToList(), CommonConversions.ConvertIdentifier(parentType.BlockStatement.Identifier), shouldAddTypeWideInitToThisPart, requiresInitializeComponent);

            async Task<IEnumerable<MemberDeclarationSyntax>> GetDirectlyConvertMembers()
            {
                return await members.SelectManyAsync(async member =>
                    new[]{await ConvertMember(member)}.Concat(GetAdditionalDeclarations(member)));

            }
        }

        private bool ShouldAddTypeWideInitToThisPart(VBSyntax.TypeBlockSyntax typeSyntax, INamedTypeSymbol namedTypeSybol)
        {
            var bestPartToAddTo = namedTypeSybol.DeclaringSyntaxReferences
                .OrderByDescending(l => l.SyntaxTree.FilePath?.IsGeneratedFile() == false).ThenBy(l => l.GetSyntax() is VBSyntax.TypeBlockSyntax tbs && HasAttribute(tbs, "DesignerGenerated"))
                .First();
            return bestPartToAddTo.SyntaxTree == typeSyntax.SyntaxTree && bestPartToAddTo.Span.OverlapsWith(typeSyntax.Span);
        }

        private static bool HasAttribute(VBSyntax.TypeBlockSyntax tbs, string attributeName)
        {
            return tbs.BlockStatement.AttributeLists.Any(list => list.Attributes.Any(a => a.Name.GetText().ToString().Contains(attributeName)));
        }

        private MemberDeclarationSyntax[] GetAdditionalDeclarations(VBSyntax.StatementSyntax member)
        {
            if (_additionalDeclarations.TryGetValue(member, out var additionalStatements))
            {
                _additionalDeclarations.Remove(member);
                return additionalStatements;
            }

            return Array.Empty<MemberDeclarationSyntax>();
        }

        /// <summary>
        /// In case of error, creates a dummy class to attach the error comment to.
        /// This is because:
        /// * Empty statements are invalid in many contexts in C#.
        /// * There may be no previous node to attach to.
        /// * Attaching to a parent would result in the code being out of order from where it was originally.
        /// </summary>
        private async Task<MemberDeclarationSyntax> ConvertMember(VBSyntax.StatementSyntax member)
        {
            try {
                return (MemberDeclarationSyntax) await member.AcceptAsync(TriviaConvertingDeclarationVisitor);
            } catch (Exception e) {
                return CreateErrorMember(member, e);
            }

            MemberDeclarationSyntax CreateErrorMember(VBSyntax.StatementSyntax memberCausingError, Exception e)
            {
                var dummyClass
                    = SyntaxFactory.ClassDeclaration("_failedMemberConversionMarker" + ++_failedMemberConversionMarkerCount);
                return dummyClass.WithCsTrailingErrorComment(memberCausingError, e);
            }
        }

        public override async Task<CSharpSyntaxNode> VisitClassBlock(VBSyntax.ClassBlockSyntax node)
        {
            _accessedThroughMyClass = GetMyClassAccessedNames(node);
            var classStatement = node.ClassStatement;
            var attributes = await CommonConversions.ConvertAttributes(classStatement.AttributeLists);
            var (parameters, constraints) = await SplitTypeParameters(classStatement.TypeParameterList);
            var convertedIdentifier = CommonConversions.ConvertIdentifier(classStatement.Identifier);

            return SyntaxFactory.ClassDeclaration(
                attributes, ConvertTypeBlockModifiers(classStatement, TokenContext.Global),
                convertedIdentifier,
                parameters,
                await ConvertInheritsAndImplements(node.Inherits, node.Implements),
                constraints,
                SyntaxFactory.List(await ConvertMembers(node))
            );
        }

        private async Task<BaseListSyntax> ConvertInheritsAndImplements(SyntaxList<VBSyntax.InheritsStatementSyntax> inherits, SyntaxList<VBSyntax.ImplementsStatementSyntax> implements)
        {
            if (inherits.Count + implements.Count == 0)
                return null;
            var baseTypes = new List<BaseTypeSyntax>();
            foreach (var t in inherits.SelectMany(c => c.Types).Concat(implements.SelectMany(c => c.Types)))
                baseTypes.Add(SyntaxFactory.SimpleBaseType((TypeSyntax) await t.AcceptAsync(_triviaConvertingExpressionVisitor)));
            return SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(baseTypes));
        }

        public override async Task<CSharpSyntaxNode> VisitModuleBlock(VBSyntax.ModuleBlockSyntax node)
        {
            var stmt = node.ModuleStatement;
            var attributes = await CommonConversions.ConvertAttributes(stmt.AttributeLists);
            var members = SyntaxFactory.List(await ConvertMembers(node));
            var (parameters, constraints) = await SplitTypeParameters(stmt.TypeParameterList);

            return SyntaxFactory.ClassDeclaration(
                attributes, ConvertTypeBlockModifiers(stmt, TokenContext.InterfaceOrModule, Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword),
                CommonConversions.ConvertIdentifier(stmt.Identifier),
                parameters,
                await ConvertInheritsAndImplements(node.Inherits, node.Implements),
                constraints,
                members
            );
        }

        public override async Task<CSharpSyntaxNode> VisitStructureBlock(VBSyntax.StructureBlockSyntax node)
        {
            var stmt = node.StructureStatement;
            var attributes = await CommonConversions.ConvertAttributes(stmt.AttributeLists);
            var members = SyntaxFactory.List(await ConvertMembers(node));

            var (parameters, constraints) = await SplitTypeParameters(stmt.TypeParameterList);

            return SyntaxFactory.StructDeclaration(
                attributes, ConvertTypeBlockModifiers(stmt, TokenContext.Global), CommonConversions.ConvertIdentifier(stmt.Identifier),
                parameters,
                await ConvertInheritsAndImplements(node.Inherits, node.Implements),
                constraints,
                members
            );
        }

        public override async Task<CSharpSyntaxNode> VisitInterfaceBlock(VBSyntax.InterfaceBlockSyntax node)
        {
            var stmt = node.InterfaceStatement;
            var attributes = await CommonConversions.ConvertAttributes(stmt.AttributeLists);
            var members = SyntaxFactory.List(await ConvertMembers(node));

            var (parameters, constraints) = await SplitTypeParameters(stmt.TypeParameterList);

            return SyntaxFactory.InterfaceDeclaration(
                attributes, ConvertTypeBlockModifiers(stmt, TokenContext.InterfaceOrModule), CommonConversions.ConvertIdentifier(stmt.Identifier),
                parameters,
                await ConvertInheritsAndImplements(node.Inherits, node.Implements),
                constraints,
                members
            );
        }

        private SyntaxTokenList ConvertTypeBlockModifiers(VBSyntax.TypeStatementSyntax stmt,
            TokenContext interfaceOrModule, params Microsoft.CodeAnalysis.CSharp.SyntaxKind[] extraModifiers)
        {
            if (IsPartialType(stmt) && !HasPartialKeyword(stmt.Modifiers)) {
                extraModifiers = extraModifiers.Concat(new[] { Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword})
                    .ToArray();
            }

            return CommonConversions.ConvertModifiers(stmt, stmt.Modifiers, interfaceOrModule, extraCsModifierKinds: extraModifiers);
        }

        private static bool HasPartialKeyword(SyntaxTokenList modifiers)
        {
            return modifiers.Any(m => m.IsKind(VBasic.SyntaxKind.PartialKeyword));
        }

        private bool IsPartialType(VBSyntax.DeclarationStatementSyntax stmt)
        {
            return _semanticModel.GetDeclaredSymbol(stmt).IsPartialClassDefinition();
        }

        public override async Task<CSharpSyntaxNode> VisitEnumBlock(VBSyntax.EnumBlockSyntax node)
        {
            var stmt = node.EnumStatement;
            // we can cast to SimpleAsClause because other types make no sense as enum-type.
            var asClause = (VBSyntax.SimpleAsClauseSyntax)stmt.UnderlyingType;
            var attributes = await stmt.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttribute);
            BaseListSyntax baseList = null;
            if (asClause != null) {
                baseList = SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(SyntaxFactory.SimpleBaseType((TypeSyntax) await asClause.Type.AcceptAsync(_triviaConvertingExpressionVisitor))));
                if (asClause.AttributeLists.Count > 0) {
                    var attributeLists = await asClause.AttributeLists.SelectManyAsync(l => CommonConversions.ConvertAttribute(l));
                    attributes = attributes.Concat(
                        SyntaxFactory.AttributeList(
                            SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)),
                            SyntaxFactory.SeparatedList(attributeLists.SelectMany(a => a.Attributes)))
                    ).ToArray();
                }
            }
            var members = SyntaxFactory.SeparatedList(await node.Members.SelectAsync(async m => (EnumMemberDeclarationSyntax) await m.AcceptAsync(TriviaConvertingDeclarationVisitor)));
            return SyntaxFactory.EnumDeclaration(
                SyntaxFactory.List(attributes), CommonConversions.ConvertModifiers(stmt, stmt.Modifiers, TokenContext.Global), CommonConversions.ConvertIdentifier(stmt.Identifier),
                baseList,
                members
            );
        }

        public override async Task<CSharpSyntaxNode> VisitEnumMemberDeclaration(VBSyntax.EnumMemberDeclarationSyntax node)
        {
            var attributes = await CommonConversions.ConvertAttributes(node.AttributeLists);
            return SyntaxFactory.EnumMemberDeclaration(
                attributes, CommonConversions.ConvertIdentifier(node.Identifier),
                (EqualsValueClauseSyntax) await node.Initializer.AcceptAsync(_triviaConvertingExpressionVisitor)
            );
        }

        public override async Task<CSharpSyntaxNode> VisitDelegateStatement(VBSyntax.DelegateStatementSyntax node)
        {
            var attributes = await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttribute);

            var (typeParameters, constraints) = await SplitTypeParameters(node.TypeParameterList);

            TypeSyntax returnType;
            var asClause = node.AsClause;
            if (asClause == null) {
                returnType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword));
            } else {
                returnType = (TypeSyntax) await asClause.Type.AcceptAsync(_triviaConvertingExpressionVisitor);
                if (asClause.AttributeLists.Count > 0) {
                    var attributeListSyntaxs = await asClause.AttributeLists.SelectManyAsync(l => CommonConversions.ConvertAttribute(l));
                    attributes = attributes.Concat(
                        SyntaxFactory.AttributeList(
                            SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)),
                            SyntaxFactory.SeparatedList(attributeListSyntaxs.SelectMany(a => a.Attributes)))
                        ).ToArray();
                }
            }

            return SyntaxFactory.DelegateDeclaration(
                SyntaxFactory.List(attributes), CommonConversions.ConvertModifiers(node, node.Modifiers, TokenContext.Global),
                returnType, CommonConversions.ConvertIdentifier(node.Identifier),
                typeParameters,
                (ParameterListSyntax) await node.ParameterList.AcceptAsync(_triviaConvertingExpressionVisitor),
                constraints
            );
        }

        #endregion

        #region Type Members

        public override async Task<CSharpSyntaxNode> VisitFieldDeclaration(VBSyntax.FieldDeclarationSyntax node)
        {
            _additionalLocals.PushScope();
            List<MemberDeclarationSyntax> declarations;
            try {
                declarations = await GetMemberDeclarations(node);
            } finally {
                _additionalLocals.PopScope();
            }
            _additionalDeclarations.Add(node, declarations.Skip(1).ToArray());
            return declarations.First();
        }

        private async Task<List<MemberDeclarationSyntax>> GetMemberDeclarations(VBSyntax.FieldDeclarationSyntax node)
        {
            var attributes = (await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttribute)).ToList();
            var convertableModifiers =
                node.Modifiers.Where(m => !SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.WithEventsKeyword));
            var isWithEvents = node.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.WithEventsKeyword));
            var convertedModifiers =
                CommonConversions.ConvertModifiers(node.Declarators[0].Names[0], convertableModifiers.ToList(), GetMemberContext(node));
            var declarations = new List<MemberDeclarationSyntax>(node.Declarators.Count);

            foreach (var declarator in node.Declarators)
            {
                var splitDeclarations = await CommonConversions.SplitVariableDeclarations(declarator, preferExplicitType: true);
                declarations.AddRange(CreateMemberDeclarations(splitDeclarations.Variables, isWithEvents, convertedModifiers, attributes));
                declarations.AddRange(splitDeclarations.Methods.Cast<MemberDeclarationSyntax>());
            }

            return declarations;
        }

        private IEnumerable<MemberDeclarationSyntax> CreateMemberDeclarations(IReadOnlyCollection<(VariableDeclarationSyntax Decl, ITypeSymbol Type)> splitDeclarationVariables,
            bool isWithEvents, SyntaxTokenList convertedModifiers, List<AttributeListSyntax> attributes)
        {
            foreach (var (decl, type) in splitDeclarationVariables)
            {
                if (isWithEvents) {
                    var fieldDecls = CreateWithEventsMembers(convertedModifiers, attributes, decl);
                    foreach (var f in fieldDecls) yield return f;
                } else
                {
                    if (_additionalLocals.Count() > 0) {
                        foreach (var additionalDecl in CreateAdditionalLocalMembers(convertedModifiers, attributes, decl)) {
                            yield return additionalDecl;
                        }
                    } else {
                        if (type?.SpecialType == SpecialType.System_DateTime) {
                            var index = convertedModifiers.IndexOf(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstKeyword);
                            if (index >= 0) {
                                convertedModifiers = convertedModifiers.Replace(convertedModifiers[index], SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword));
                            }
                        }
                        yield return SyntaxFactory.FieldDeclaration(SyntaxFactory.List(attributes), convertedModifiers, decl);
                    }

                    
                }
            }
        }

        private IEnumerable<MemberDeclarationSyntax> CreateWithEventsMembers(SyntaxTokenList convertedModifiers, List<AttributeListSyntax> attributes, VariableDeclarationSyntax decl)
        {
            _extraUsingDirectives.Add("System.Runtime.CompilerServices");
            var initializers = decl.Variables
                .Where(a => a.Initializer != null)
                .ToDictionary(v => v.Identifier.Text, v => v.Initializer);
            var fieldDecl = decl.RemoveNodes(initializers.Values, SyntaxRemoveOptions.KeepNoTrivia);
            var initializerCollection = convertedModifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword))
                ? _additionalInitializers.AdditionalStaticInitializers
                : _additionalInitializers.AdditionalInstanceInitializers;
            foreach (var initializer in initializers) {
                initializerCollection.Add((SyntaxFactory.IdentifierName(initializer.Key), Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression, initializer.Value.Value));
            }

            var fieldDecls = _methodsWithHandles.GetDeclarationsForFieldBackedProperty(fieldDecl,
                convertedModifiers, SyntaxFactory.List(attributes));
            return fieldDecls;
        }

        private IEnumerable<MemberDeclarationSyntax> CreateAdditionalLocalMembers(SyntaxTokenList convertedModifiers, List<AttributeListSyntax> attributes, VariableDeclarationSyntax decl)
        {
            if (decl.Variables.Count > 1) {
                // Currently no way to tell which _additionalLocals would apply to which initializer
                throw new NotImplementedException(
                    "Fields with multiple declarations and initializers with ByRef parameters not currently supported");
            }

            var v = decl.Variables.First();
            var invocations = v.Initializer.Value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray();
            if (invocations.Length > 1) {
                throw new NotImplementedException(
                    "Field initializers with nested method calls not currently supported");
            }

            var invocationExpressionSyntax = invocations.First();
            var methodName = invocationExpressionSyntax.Expression
                .ChildNodes().OfType<SimpleNameSyntax>().Last();
            var newMethodName = $"{methodName.Identifier.ValueText}_{v.Identifier.ValueText}";
            var localVars = _additionalLocals.Select(l => l.Value)
                .Select(al =>
                    SyntaxFactory.LocalDeclarationStatement(
                        CommonConversions.CreateVariableDeclarationAndAssignment(al.Prefix, al.Initializer)))
                .Cast<StatementSyntax>().ToList();
            var newInitializer = v.Initializer.Value.ReplaceNodes(
                v.Initializer.Value.GetAnnotatedNodes(AdditionalLocals.Annotation), (an, _) => {
                                // This should probably use a unique name like in MethodBodyVisitor - a collision is far less likely here
                                var id = ((IdentifierNameSyntax)an).Identifier.ValueText;
                    return SyntaxFactory.IdentifierName(_additionalLocals[id].Prefix);
                });
            var body = SyntaxFactory.Block(
                localVars.Concat(SyntaxFactory.SingletonList(SyntaxFactory.ReturnStatement(newInitializer))));
            var methodAttrs = SyntaxFactory.List<AttributeListSyntax>();
            // Method calls in initializers must be static in C# - Supporting this is #281
            var modifiers = SyntaxFactory.TokenList(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword));
            var typeConstraints = SyntaxFactory.List<TypeParameterConstraintClauseSyntax>();
            var parameterList = SyntaxFactory.ParameterList();
            var methodDecl = SyntaxFactory.MethodDeclaration(methodAttrs, modifiers, decl.Type, null,
                SyntaxFactory.Identifier(newMethodName), null, parameterList, typeConstraints, body, null);
            yield return methodDecl;

            var newVar =
                v.WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(newMethodName))));
            var newVarDecl =
                SyntaxFactory.VariableDeclaration(decl.Type, SyntaxFactory.SingletonSeparatedList(newVar));

            yield return SyntaxFactory.FieldDeclaration(SyntaxFactory.List(attributes), convertedModifiers, newVarDecl);
        }

        private List<MethodWithHandles> GetMethodWithHandles(VBSyntax.TypeBlockSyntax parentType)
        {
            if (parentType == null || !(this._semanticModel.GetDeclaredSymbol((global::Microsoft.CodeAnalysis.SyntaxNode)parentType) is ITypeSymbol containingType)) return new List<MethodWithHandles>();

            var methodWithHandleses = containingType.GetMembers().OfType<IMethodSymbol>()
                .Where(m => HandledEvents(m).Any())
                .Select(m => {
                    var ids = HandledEvents(m)
                        .Select(p => (SyntaxFactory.Identifier(GetCSharpIdentifierText(p.EventContainer)), CommonConversions.ConvertIdentifier(p.EventMember.Identifier, sourceTriviaMapKind: SourceTriviaMapKind.None), p.Event, p.ParametersToDiscard))
                        .ToList();
                    var csFormIds = ids.Where(id => id.Item1.Text == "this" || id.Item1.Text == "base").ToList();
                    var csPropIds = ids.Except(csFormIds).ToList();
                    if (!csPropIds.Any() && !csFormIds.Any()) return null;
                    var csMethodId = SyntaxFactory.Identifier(m.Name);
                    var delegatingMethodBaseForHandler = (MethodDeclarationSyntax) _csSyntaxGenerator.MethodDeclaration(m);
                    return new MethodWithHandles(_csSyntaxGenerator, csMethodId, csPropIds, csFormIds);
                }).Where(x => x != null).ToList();
            return methodWithHandleses;

            string GetCSharpIdentifierText(VBSyntax.EventContainerSyntax p)
            {
                switch (p) {
                    //For me, trying to use "MyClass" in a Handles expression is a syntax error. Events aren't overridable anyway so I'm not sure how this would get used.
                    case VBSyntax.KeywordEventContainerSyntax kecs when kecs.Keyword.IsKind(VBasic.SyntaxKind.MyBaseKeyword):
                        return "base";
                    case VBSyntax.KeywordEventContainerSyntax _:
                        return "this";
                    default:
                        return CommonConversions.CsEscapedIdentifier(p.GetText().ToString()).Text;
                }
            }
        }

        /// <summary>
        /// VBasic.VisualBasicExtensions.HandledEvents(m) seems to optimize away some events, so just detect from syntax
        /// </summary>
        private List<(VBSyntax.EventContainerSyntax EventContainer, VBSyntax.IdentifierNameSyntax EventMember, IEventSymbol Event, int ParametersToDiscard)> HandledEvents(IMethodSymbol m)
        {
            return m.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VBSyntax.MethodStatementSyntax>()
                .Where(mbb => mbb.HandlesClause?.Events.Any() == true)
                .SelectMany(mbb => HandledEvent(mbb))
                .ToList();
        }

        private IEnumerable<(VBSyntax.EventContainerSyntax EventContainer, VBSyntax.IdentifierNameSyntax EventMember, IEventSymbol Event, int ParametersToDiscard)> HandledEvent(VBSyntax.MethodStatementSyntax mbb)
        {
            var mayRequireDiscardedParameters = !mbb.ParameterList.Parameters.Any();
            //TODO: PERF: Get group by syntax tree and get semantic model once in case it doesn't get succesfully cached
            var semanticModel = mbb.SyntaxTree == _semanticModel.SyntaxTree ? _semanticModel : _compilation.GetSemanticModel(mbb.SyntaxTree, ignoreAccessibility: true);
            return mbb.HandlesClause.Events.Select(e => {
                var symbol = semanticModel.GetSymbolInfo(e.EventMember).Symbol as IEventSymbol;
                var toDiscard = mayRequireDiscardedParameters ? symbol?.Type.GetDelegateInvokeMethod()?.GetParameters().Count() ?? 0 : 0;
                var symbolParameters = symbol?.GetParameters();
                return (e.EventContainer, e.EventMember, Event: symbol, toDiscard);
            });
        }

        public override async Task<CSharpSyntaxNode> VisitPropertyStatement(VBSyntax.PropertyStatementSyntax node)
        {
            var attributes = await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttribute);
            var isReadonly = node.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.ReadOnlyKeyword));
            var isWriteOnly = node.Modifiers.Any(m => SyntaxTokenExtensions.IsKind(m, VBasic.SyntaxKind.WriteOnlyKeyword));
            var convertibleModifiers = node.Modifiers.Where(m => !m.IsKind(VBasic.SyntaxKind.ReadOnlyKeyword, VBasic.SyntaxKind.WriteOnlyKeyword, VBasic.SyntaxKind.DefaultKeyword));
            var modifiers = CommonConversions.ConvertModifiers(node, convertibleModifiers.ToList(), GetMemberContext(node));
            var isIndexer = CommonConversions.IsDefaultIndexer(node);
            var accessedThroughMyClass = IsAccessedThroughMyClass(node, node.Identifier, ModelExtensions.GetDeclaredSymbol(_semanticModel, node));
            bool hasImplementation = !node.Modifiers.Any(m => m.IsKind(VBasic.SyntaxKind.MustOverrideKeyword)) && node.GetAncestor<VBSyntax.InterfaceBlockSyntax>() == null;

            var initializer = (EqualsValueClauseSyntax) await node.Initializer.AcceptAsync(_triviaConvertingExpressionVisitor);
            VBSyntax.TypeSyntax vbType;
            switch (node.AsClause) {
                case VBSyntax.SimpleAsClauseSyntax c:
                    vbType = c.Type;
                    break;
                case VBSyntax.AsNewClauseSyntax c:
                    initializer = SyntaxFactory.EqualsValueClause((ExpressionSyntax)await c.NewExpression.AcceptAsync(_triviaConvertingExpressionVisitor));
                    vbType = VBasic.SyntaxExtensions.Type(c.NewExpression);
                    break;
                case null:
                    vbType = null;
                    break;
                default:
                    throw new NotImplementedException($"{node.AsClause.GetType().FullName} not implemented!");
            }

            var rawType = (TypeSyntax) await vbType.AcceptAsync(_triviaConvertingExpressionVisitor)
                ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ObjectKeyword));

            AccessorListSyntax accessors = null;
            if (node.Parent is VBSyntax.PropertyBlockSyntax propertyBlock) {
                if (node.ParameterList?.Parameters.Any() == true && !isIndexer) {
                    if (accessedThroughMyClass) {
                        // Would need to create a delegating implementation to implement this
                        throw new NotImplementedException("MyClass indexing not implemented");
                    }

                    var methodDeclarationSyntaxs = await propertyBlock.Accessors.SelectAsync(async a =>
                        (MethodDeclarationSyntax) await a.AcceptAsync(TriviaConvertingDeclarationVisitor));
                    var accessorMethods = methodDeclarationSyntaxs.Select(WithMergedModifiers).ToArray();
                    _additionalDeclarations.Add(propertyBlock, accessorMethods.Skip(1).ToArray());
                    return accessorMethods[0];
                }

                accessors = SyntaxFactory.AccessorList(
                    SyntaxFactory.List(
                        (await propertyBlock.Accessors.SelectAsync(async a =>
                            (AccessorDeclarationSyntax) await a.AcceptAsync(TriviaConvertingDeclarationVisitor))
                        )
                    ));
            } else {
                accessors = ConvertSimpleAccessors(isWriteOnly, isReadonly, hasImplementation);
            }


            if (isIndexer) {
                if (accessedThroughMyClass) {
                    // Not sure if this is possible
                    throw new NotImplementedException("MyClass indexing not implemented");
                }

                var parameters = await node.ParameterList.Parameters.SelectAsync(async p => (ParameterSyntax) await p.AcceptAsync(_triviaConvertingExpressionVisitor));
                var parameterList = SyntaxFactory.BracketedParameterList(SyntaxFactory.SeparatedList(parameters));
                return SyntaxFactory.IndexerDeclaration(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    rawType,
                    null,
                    parameterList,
                    accessors
                );
            } else {
                var csIdentifier = CommonConversions.ConvertIdentifier(node.Identifier);

                if (accessedThroughMyClass) {
                    string csIndentifierName = AddRealPropertyDelegatingToMyClassVersion(node, csIdentifier, attributes, modifiers, rawType);
                    modifiers = modifiers.Remove(modifiers.Single(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VirtualKeyword)));
                    csIdentifier = SyntaxFactory.Identifier(csIndentifierName);
                }

                var semicolonToken = SyntaxFactory.Token(initializer == null ? Microsoft.CodeAnalysis.CSharp.SyntaxKind.None : Microsoft.CodeAnalysis.CSharp.SyntaxKind.SemicolonToken);
                return SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    rawType,
                    null,
                    csIdentifier, accessors,
                    null,
                    initializer,
                    semicolonToken);
            }

            MemberDeclarationSyntax WithMergedModifiers(MethodDeclarationSyntax member)
            {
                SyntaxTokenList originalModifiers = member.GetModifiers();
                var hasVisibility = originalModifiers.Any(m => m.IsCsVisibility(false, false));
                var modifiersToAdd = hasVisibility ? modifiers.Where(m => !m.IsCsVisibility(false, false)) : modifiers;
                return member.WithModifiers(SyntaxFactory.TokenList(originalModifiers.Concat(modifiersToAdd)));
            }
        }

        private string AddRealPropertyDelegatingToMyClassVersion(VBSyntax.PropertyStatementSyntax node, SyntaxToken csIdentifier,
            AttributeListSyntax[] attributes, SyntaxTokenList modifiers, TypeSyntax rawType)
        {
            var csIndentifierName = "MyClass" + csIdentifier.ValueText;
            ExpressionSyntax thisDotIdentifier = SyntaxFactory.ParseExpression($"this.{csIndentifierName}");
            var getReturn = SyntaxFactory.Block(SyntaxFactory.ReturnStatement(thisDotIdentifier));
            var getAccessor = SyntaxFactory.AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration, getReturn);
            var setValue = SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression, thisDotIdentifier,
                    SyntaxFactory.IdentifierName("value"))));
            var setAccessor = SyntaxFactory.AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration, setValue);
            var realAccessors = SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {getAccessor, setAccessor}));
            var realDecl = SyntaxFactory.PropertyDeclaration(
                SyntaxFactory.List(attributes),
                modifiers,
                rawType,
                null,
                csIdentifier, realAccessors,
                null,
                null,
                SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None));

            _additionalDeclarations.Add(node, new MemberDeclarationSyntax[] { realDecl });
            return csIndentifierName;
        }

        private static AccessorListSyntax ConvertSimpleAccessors(bool isWriteOnly, bool isReadonly, bool hasImplementation)
        {
            AccessorListSyntax accessors;
            var getAccessor = SyntaxFactory.AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SemicolonToken);
            var setAccessor = SyntaxFactory.AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SemicolonToken);
            if (isWriteOnly)
            {
                getAccessor = getAccessor.AddModifiers(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword));
            }

            if (isReadonly)
            {
                setAccessor = setAccessor.AddModifiers(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword));
            }

            if (!hasImplementation && isReadonly)
            {
                accessors = SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {getAccessor}));
            }
            else if (!hasImplementation && isWriteOnly)
            {
                accessors = SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {setAccessor}));
            }
            else
            {
                // In VB, there's a backing field which can always be read and written to even on ReadOnly/WriteOnly properties.
                // Our conversion will rewrite usages of that field to use the property accessors which therefore must exist and be private at minimum.
                accessors = SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {getAccessor, setAccessor}));
            }

            return accessors;
        }

        public override async Task<CSharpSyntaxNode> VisitPropertyBlock(VBSyntax.PropertyBlockSyntax node)
        {
            return await node.PropertyStatement.AcceptAsync(TriviaConvertingDeclarationVisitor, SourceTriviaMapKind.SubNodesOnly);
        }

        public override async Task<CSharpSyntaxNode> VisitAccessorBlock(VBSyntax.AccessorBlockSyntax node)
        {
            Microsoft.CodeAnalysis.CSharp.SyntaxKind blockKind;
            bool isIterator = node.IsIterator();
            var csReturnVariableOrNull = CommonConversions.GetRetVariableNameOrNull(node);
            var convertedStatements = await ConvertStatements(node.Statements, await CreateMethodBodyVisitor(node, isIterator));
            var body = WithImplicitReturnStatements(node, convertedStatements, csReturnVariableOrNull);
            var attributes = await CommonConversions.ConvertAttributes(node.AccessorStatement.AttributeLists);
            var modifiers = CommonConversions.ConvertModifiers(node, node.AccessorStatement.Modifiers, TokenContext.Local);
            string potentialMethodId;
            var ancestoryPropertyBlock = node.GetAncestor<VBSyntax.PropertyBlockSyntax>();
            var containingPropertyStmt = ancestoryPropertyBlock?.PropertyStatement;
            var sourceMap = ancestoryPropertyBlock?.Accessors.FirstOrDefault() == node ? SourceTriviaMapKind.All : SourceTriviaMapKind.None;
            switch (node.Kind()) {
                case VBasic.SyntaxKind.GetAccessorBlock:
                    blockKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration;
                    potentialMethodId = $"get_{(containingPropertyStmt.Identifier.Text)}";

                    if (containingPropertyStmt.AsClause is VBSyntax.SimpleAsClauseSyntax getAsClause &&
                        await ShouldConvertAsParameterizedProperty()) {
                        var method = await CreateMethodDeclarationSyntax(containingPropertyStmt?.ParameterList);
                        return method.WithReturnType((TypeSyntax) await getAsClause.Type.AcceptAsync(_triviaConvertingExpressionVisitor, sourceMap));
                    }
                    break;
                case VBasic.SyntaxKind.SetAccessorBlock:
                    blockKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration;
                    potentialMethodId = $"set_{(containingPropertyStmt.Identifier.Text)}";

                    if (containingPropertyStmt.AsClause is VBSyntax.SimpleAsClauseSyntax setAsClause && await ShouldConvertAsParameterizedProperty()) {
                        var setMethod = await CreateMethodDeclarationSyntax(containingPropertyStmt?.ParameterList);
                        var valueParameterType = (TypeSyntax) await setAsClause.Type.AcceptAsync(_triviaConvertingExpressionVisitor, sourceMap);
                        return setMethod.AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(valueParameterType));
                    }
                    break;
                case VBasic.SyntaxKind.AddHandlerAccessorBlock:
                    blockKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddAccessorDeclaration;
                    break;
                case VBasic.SyntaxKind.RemoveHandlerAccessorBlock:
                    blockKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind.RemoveAccessorDeclaration;
                    break;
                case VBasic.SyntaxKind.RaiseEventAccessorBlock:
                    var eventStatement = ((VBSyntax.EventBlockSyntax)node.Parent).EventStatement;
                    var eventName = CommonConversions.ConvertIdentifier(eventStatement.Identifier).ValueText;
                    potentialMethodId = $"On{eventName}";
                    return await CreateMethodDeclarationSyntax(node.AccessorStatement.ParameterList);
                default:
                    throw new NotSupportedException(node.Kind().ToString());
            }

            return SyntaxFactory.AccessorDeclaration(blockKind, attributes, modifiers, body);

            async Task<bool> ShouldConvertAsParameterizedProperty()
            {
                if (containingPropertyStmt.ParameterList?.Parameters.Any() == true && !CommonConversions.IsDefaultIndexer(containingPropertyStmt)) {
                    return true;
                }

                return false;
            }

            async Task<MethodDeclarationSyntax> CreateMethodDeclarationSyntax(VBSyntax.ParameterListSyntax containingPropParameterList)
            {
                var parameterListSyntax = await containingPropParameterList.AcceptAsync(_triviaConvertingExpressionVisitor, sourceMap);
                MethodDeclarationSyntax methodDeclarationSyntax = SyntaxFactory.MethodDeclaration(attributes, modifiers,
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword)), null,
                    SyntaxFactory.Identifier(potentialMethodId), null,
                    (ParameterListSyntax) parameterListSyntax, SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(), body, null);
                return methodDeclarationSyntax;
            }
        }

        public override async Task<CSharpSyntaxNode> VisitAccessorStatement(VBSyntax.AccessorStatementSyntax node)
        {
            return SyntaxFactory.AccessorDeclaration(node.Kind().ConvertToken(), null);
        }

        public override async Task<CSharpSyntaxNode> VisitMethodBlock(VBSyntax.MethodBlockSyntax node)
        {
            var methodBlock = (BaseMethodDeclarationSyntax) await node.SubOrFunctionStatement.AcceptAsync(TriviaConvertingDeclarationVisitor, SourceTriviaMapKind.SubNodesOnly);

            var declaredSymbol = ModelExtensions.GetDeclaredSymbol(_semanticModel, node);
            if (!declaredSymbol.CanHaveMethodBody()) {
                return methodBlock;
            }
            var csReturnVariableOrNull = CommonConversions.GetRetVariableNameOrNull(node);
            var visualBasicSyntaxVisitor = await CreateMethodBodyVisitor(node, node.IsIterator(), csReturnVariableOrNull);
            var convertedStatements = await ConvertStatements(node.Statements, visualBasicSyntaxVisitor);

            if (node.SubOrFunctionStatement.Identifier.Text == "InitializeComponent" && node.SubOrFunctionStatement.IsKind(VBasic.SyntaxKind.SubStatement) && declaredSymbol.ContainingType.IsDesignerGeneratedTypeWithInitializeComponent(_compilation)) {
                var firstResumeLayout = convertedStatements.Statements.FirstOrDefault(IsThisResumeLayoutInvocation) ?? convertedStatements.Statements.Last();
                convertedStatements = convertedStatements.InsertNodesBefore(firstResumeLayout, _methodsWithHandles.GetInitializeComponentClassEventHandlers());
            }

            var body = WithImplicitReturnStatements(node, convertedStatements, csReturnVariableOrNull);

            return methodBlock.WithBody(body);
        }

        private static bool IsThisResumeLayoutInvocation(StatementSyntax s)
        {
            return s is ExpressionStatementSyntax ess && ess.Expression is InvocationExpressionSyntax ies && ies.Expression.ToString().Equals("this.ResumeLayout");
        }

        private BlockSyntax WithImplicitReturnStatements(VBSyntax.MethodBlockBaseSyntax node, BlockSyntax convertedStatements,
            IdentifierNameSyntax csReturnVariableOrNull)
        {
            if (!node.MustReturn()) return convertedStatements;
            if (_semanticModel.GetDeclaredSymbol(node) is IMethodSymbol ms && ms.ReturnsVoidOrAsyncTask()) {
                return convertedStatements;
            }


            var preBodyStatements = new List<StatementSyntax>();
            var postBodyStatements = new List<StatementSyntax>();

            var functionSym = ModelExtensions.GetDeclaredSymbol(_semanticModel, node);
            if (functionSym != null) {
                var returnType = CommonConversions.GetTypeSyntax(functionSym.GetReturnType());

                if (csReturnVariableOrNull != null) {
                    var retDeclaration = CommonConversions.CreateVariableDeclarationAndAssignment(
                        csReturnVariableOrNull.Identifier.ValueText, SyntaxFactory.DefaultExpression(returnType),
                        returnType);
                    preBodyStatements.Add(SyntaxFactory.LocalDeclarationStatement(retDeclaration));
                }

                ControlFlowAnalysis controlFlowAnalysis = null;
                if (!node.Statements.IsEmpty())
                    controlFlowAnalysis =
                        ModelExtensions.AnalyzeControlFlow(_semanticModel, node.Statements.First(), node.Statements.Last());

                bool mayNeedReturn = controlFlowAnalysis?.EndPointIsReachable != false;
                if (mayNeedReturn) {
                    var csReturnExpression = csReturnVariableOrNull ??
                                             (ExpressionSyntax)SyntaxFactory.DefaultExpression(returnType);
                    postBodyStatements.Add(SyntaxFactory.ReturnStatement(csReturnExpression));
                }
            }

            var statements = preBodyStatements
                .Concat(convertedStatements.Statements)
                .Concat(postBodyStatements);

            return SyntaxFactory.Block(statements);
        }

        private async Task<BlockSyntax> ConvertStatements(SyntaxList<VBSyntax.StatementSyntax> statements, VBasic.VisualBasicSyntaxVisitor<Task<SyntaxList<StatementSyntax>>> methodBodyVisitor)
        {
            return SyntaxFactory.Block(await statements.SelectManyAsync(async s => (IEnumerable<StatementSyntax>) await s.Accept(methodBodyVisitor)));
        }

        private bool IsAccessedThroughMyClass(SyntaxNode node, SyntaxToken identifier, ISymbol symbolOrNull)
        {
            bool accessedThroughMyClass = false;
            if (symbolOrNull != null && symbolOrNull.IsVirtual && !symbolOrNull.IsAbstract) {
                var classBlock = node.Ancestors().OfType<VBSyntax.ClassBlockSyntax>().FirstOrDefault();
                if (classBlock != null) {
                    accessedThroughMyClass = _accessedThroughMyClass.Contains(identifier.Text);
                }
            }

            return accessedThroughMyClass;
        }

        private static HashSet<string> GetMyClassAccessedNames(VBSyntax.ClassBlockSyntax classBlock)
        {
            var memberAccesses = classBlock.DescendantNodes().OfType<VBSyntax.MemberAccessExpressionSyntax>();
            var accessedTextNames = new HashSet<string>(memberAccesses
                .Where(mae => mae.Expression is VBSyntax.MyClassExpressionSyntax)
                .Select(mae => mae.Name.Identifier.Text), StringComparer.OrdinalIgnoreCase);
            return accessedTextNames;
        }

        public override async Task<CSharpSyntaxNode> VisitMethodStatement(VBSyntax.MethodStatementSyntax node)
        {
            var attributes = await CommonConversions.ConvertAttributes(node.AttributeLists);
            bool hasBody = node.Parent is VBSyntax.MethodBlockBaseSyntax;

            if ("Finalize".Equals(node.Identifier.ValueText, StringComparison.OrdinalIgnoreCase)
                && node.Modifiers.Any(m => VBasic.VisualBasicExtensions.Kind(m) == VBasic.SyntaxKind.OverridesKeyword)) {
                var decl = SyntaxFactory.DestructorDeclaration(CommonConversions.ConvertIdentifier(node.GetAncestor<VBSyntax.TypeBlockSyntax>().BlockStatement.Identifier)
                ).WithAttributeLists(attributes);
                if (hasBody) return decl;
                return decl.WithSemicolonToken(SemicolonToken);
            } else {
                var tokenContext = GetMemberContext(node);
                var declaredSymbol = ModelExtensions.GetDeclaredSymbol(_semanticModel, node) as IMethodSymbol;
                var extraCsModifierKinds = declaredSymbol?.IsExtern == true ? new[] { Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExternKeyword} : Array.Empty<Microsoft.CodeAnalysis.CSharp.SyntaxKind>();
                var convertedModifiers = CommonConversions.ConvertModifiers(node, node.Modifiers, tokenContext, extraCsModifierKinds: extraCsModifierKinds);

                bool accessedThroughMyClass = IsAccessedThroughMyClass(node, node.Identifier, declaredSymbol);

                var isPartialDefinition = declaredSymbol.IsPartialMethodDefinition();

                if (declaredSymbol.IsPartialMethodImplementation() || isPartialDefinition) {
                    var privateModifier = convertedModifiers.SingleOrDefault(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword));
                    if (privateModifier != default(SyntaxToken)) {
                        convertedModifiers = convertedModifiers.Remove(privateModifier);
                    }
                    if (!HasPartialKeyword(node.Modifiers)) {
                        convertedModifiers = convertedModifiers.Add(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
                    }
                }
                var (typeParameters, constraints) = await SplitTypeParameters(node.TypeParameterList);

                var csIdentifier = CommonConversions.ConvertIdentifier(node.Identifier);
                // If the method is virtual, and there is a MyClass.SomeMethod() call,
                // we need to emit a non-virtual method for it to call
                var returnType = (TypeSyntax) (declaredSymbol != null ? CommonConversions.GetTypeSyntax(declaredSymbol.ReturnType) :
                    await (node.AsClause?.Type).AcceptAsync(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword)));
                if (accessedThroughMyClass)
                {
                    var identifierName = "MyClass" + csIdentifier.ValueText;
                    var arrowClause = SyntaxFactory.ArrowExpressionClause(SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(identifierName)));
                    var realDecl = SyntaxFactory.MethodDeclaration(
                        attributes,
                        convertedModifiers,
                        returnType,
                        null, CommonConversions.ConvertIdentifier(node.Identifier),
                        typeParameters,
                        (ParameterListSyntax) await node.ParameterList.AcceptAsync(_triviaConvertingExpressionVisitor, SourceTriviaMapKind.None) ?? SyntaxFactory.ParameterList(),
                        constraints,
                        null,
                        arrowClause,
                        SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SemicolonToken)
                    );

                    var declNode = (VBSyntax.StatementSyntax)node.Parent;
                    _additionalDeclarations.Add(declNode, new MemberDeclarationSyntax[] { realDecl });
                    convertedModifiers = convertedModifiers.Remove(convertedModifiers.Single(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VirtualKeyword)));
                    csIdentifier = SyntaxFactory.Identifier(identifierName);
                }

                var decl = SyntaxFactory.MethodDeclaration(
                    attributes,
                    convertedModifiers,
                    returnType,
                    null,
                    csIdentifier,
                    typeParameters,
                    (ParameterListSyntax) await node.ParameterList.AcceptAsync(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.ParameterList(),
                    constraints,
                    null,
                    null
                );
                return hasBody && declaredSymbol.CanHaveMethodBody() ? decl : decl.WithSemicolonToken(SemicolonToken);
            }
        }

        private TokenContext GetMemberContext(VBSyntax.StatementSyntax member)
        {
            var parentType = member.GetAncestorOrThis<VBSyntax.TypeBlockSyntax>();
            var parentTypeKind = parentType?.Kind();
            switch (parentTypeKind) {
                case VBasic.SyntaxKind.ModuleBlock:
                    return TokenContext.MemberInModule;
                case VBasic.SyntaxKind.ClassBlock:
                    return TokenContext.MemberInClass;
                case VBasic.SyntaxKind.InterfaceBlock:
                    return TokenContext.MemberInInterface;
                case VBasic.SyntaxKind.StructureBlock:
                    return TokenContext.MemberInStruct;
                default:
                    throw new ArgumentOutOfRangeException(nameof(member));
            }
        }

        public override async Task<CSharpSyntaxNode> VisitEventBlock(VBSyntax.EventBlockSyntax node)
        {
            var block = node.EventStatement;
            var attributes = await block.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttribute);
            var modifiers = CommonConversions.ConvertModifiers(block, block.Modifiers, GetMemberContext(node));

            var rawType = (TypeSyntax) await (block.AsClause?.Type).AcceptAsync(_triviaConvertingExpressionVisitor) ?? VarType;

            var convertedAccessors = await node.Accessors.SelectAsync(async a => await a.AcceptAsync(TriviaConvertingDeclarationVisitor));
            _additionalDeclarations.Add(node, convertedAccessors.OfType<MemberDeclarationSyntax>().ToArray());
            return SyntaxFactory.EventDeclaration(
                SyntaxFactory.List(attributes),
                modifiers,
                rawType,
                null, CommonConversions.ConvertIdentifier(block.Identifier),
                SyntaxFactory.AccessorList(SyntaxFactory.List(convertedAccessors.OfType<AccessorDeclarationSyntax>()))
            );
        }

        public override async Task<CSharpSyntaxNode> VisitEventStatement(VBSyntax.EventStatementSyntax node)
        {
            var attributes = await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttribute);
            var modifiers = CommonConversions.ConvertModifiers(node, node.Modifiers, GetMemberContext(node));
            var id = CommonConversions.ConvertIdentifier(node.Identifier);

            if (node.AsClause == null) {
                var delegateName = SyntaxFactory.Identifier(id.ValueText + "EventHandler");

                var delegateDecl = SyntaxFactory.DelegateDeclaration(
                    SyntaxFactory.List<AttributeListSyntax>(),
                    modifiers,
                    SyntaxFactory.ParseTypeName("void"),
                    delegateName,
                    null,
                    (ParameterListSyntax) await node.ParameterList.AcceptAsync(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.ParameterList(),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>()
                );

                var eventDecl = SyntaxFactory.EventFieldDeclaration(
                    SyntaxFactory.List(attributes),
                    modifiers,
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName(delegateName),
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(id)))
                );

                _additionalDeclarations.Add(node, new MemberDeclarationSyntax[] { delegateDecl });
                return eventDecl;
            }

            return SyntaxFactory.EventFieldDeclaration(
                SyntaxFactory.List(attributes),
                modifiers,
                SyntaxFactory.VariableDeclaration((TypeSyntax) await node.AsClause.Type.AcceptAsync(_triviaConvertingExpressionVisitor),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(id)))
            );
        }

        public override async Task<CSharpSyntaxNode> VisitOperatorBlock(VBSyntax.OperatorBlockSyntax node)
        {
            return await node.BlockStatement.AcceptAsync(TriviaConvertingDeclarationVisitor, SourceTriviaMapKind.SubNodesOnly);
        }

        public override async Task<CSharpSyntaxNode> VisitOperatorStatement(VBSyntax.OperatorStatementSyntax node)
        {
            var containingBlock = (VBSyntax.OperatorBlockSyntax) node.Parent;
            var attributes = await node.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttribute);
            var attributeList = SyntaxFactory.List(attributes);
            var returnType = (TypeSyntax) await (node.AsClause?.Type).AcceptAsync(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword));
            var parameterList = (ParameterListSyntax) await node.ParameterList.AcceptAsync(_triviaConvertingExpressionVisitor);
            var methodBodyVisitor = await CreateMethodBodyVisitor(node);
            var body = SyntaxFactory.Block(await containingBlock.Statements.SelectManyAsync(async s => (IEnumerable<StatementSyntax>) await s.Accept(methodBodyVisitor)));
            var modifiers = CommonConversions.ConvertModifiers(node, node.Modifiers, GetMemberContext(node));

            var conversionModifiers = modifiers.Where(CommonConversions.IsConversionOperator).ToList();
            var nonConversionModifiers = SyntaxFactory.TokenList(modifiers.Except(conversionModifiers));

            if (conversionModifiers.Any()) {
                return SyntaxFactory.ConversionOperatorDeclaration(attributeList, nonConversionModifiers,
                    conversionModifiers.Single(), returnType, parameterList, body, null);
            }

            return SyntaxFactory.OperatorDeclaration(attributeList, nonConversionModifiers, returnType, node.OperatorToken.ConvertToken(), parameterList, body, null);
        }

        public override async Task<CSharpSyntaxNode> VisitConstructorBlock(VBSyntax.ConstructorBlockSyntax node)
        {
            var block = node.BlockStatement;
            var attributes = await block.AttributeLists.SelectManyAsync(CommonConversions.ConvertAttribute);
            var modifiers = CommonConversions.ConvertModifiers(block, block.Modifiers, GetMemberContext(node));

            var ctor = (node.Statements.FirstOrDefault() as VBSyntax.ExpressionStatementSyntax)?.Expression as VBSyntax.InvocationExpressionSyntax;
            var ctorExpression = ctor?.Expression as VBSyntax.MemberAccessExpressionSyntax;
            var ctorArgs = (ArgumentListSyntax) await (ctor?.ArgumentList).AcceptAsync(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.ArgumentList();

            IEnumerable<VBSyntax.StatementSyntax> statements;
            ConstructorInitializerSyntax ctorCall;
            if (ctorExpression == null || !ctorExpression.Name.Identifier.IsKindOrHasMatchingText(VBasic.SyntaxKind.NewKeyword)) {
                statements = node.Statements;
                ctorCall = null;
            } else if (ctorExpression.Expression is VBSyntax.MyBaseExpressionSyntax) {
                statements = node.Statements.Skip(1);
                ctorCall = SyntaxFactory.ConstructorInitializer(Microsoft.CodeAnalysis.CSharp.SyntaxKind.BaseConstructorInitializer, ctorArgs);
            } else if (ctorExpression.Expression is VBSyntax.MeExpressionSyntax || ctorExpression.Expression is VBSyntax.MyClassExpressionSyntax) {
                statements = node.Statements.Skip(1);
                ctorCall = SyntaxFactory.ConstructorInitializer(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ThisConstructorInitializer, ctorArgs);
            } else {
                statements = node.Statements;
                ctorCall = null;
            }

            var methodBodyVisitor = await CreateMethodBodyVisitor(node);
            return SyntaxFactory.ConstructorDeclaration(
                SyntaxFactory.List(attributes),
                modifiers, 
                CommonConversions.ConvertIdentifier(node.GetAncestor<VBSyntax.TypeBlockSyntax>().BlockStatement.Identifier).WithoutSourceMapping(), //TODO Use semantic model for this name
                (ParameterListSyntax) await block.ParameterList.AcceptAsync(_triviaConvertingExpressionVisitor),
                ctorCall,
                SyntaxFactory.Block(await statements.SelectManyAsync(async s => (IEnumerable<StatementSyntax>) await s.Accept(methodBodyVisitor)))
            );
        }

        public override async Task<CSharpSyntaxNode> VisitDeclareStatement(VBSyntax.DeclareStatementSyntax node)
        {
            var importAttributes = new List<AttributeArgumentSyntax>();
            _extraUsingDirectives.Add(DllImportType.Namespace);
            _extraUsingDirectives.Add(CharSetType.Namespace);
            var dllImportAttributeName = SyntaxFactory.ParseName(DllImportType.Name.Replace("Attribute", ""));
            var dllImportLibLiteral = await node.LibraryName.AcceptAsync(_triviaConvertingExpressionVisitor);
            importAttributes.Add(SyntaxFactory.AttributeArgument((ExpressionSyntax)dllImportLibLiteral));

            if (node.AliasName != null) {
                importAttributes.Add(SyntaxFactory.AttributeArgument(SyntaxFactory.NameEquals("EntryPoint"), null, (ExpressionSyntax) await node.AliasName.AcceptAsync(_triviaConvertingExpressionVisitor)));
            }

            if (!node.CharsetKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)) {
                importAttributes.Add(SyntaxFactory.AttributeArgument(SyntaxFactory.NameEquals(CharSetType.Name), null, SyntaxFactory.MemberAccessExpression(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ParseTypeName(CharSetType.Name), SyntaxFactory.IdentifierName(node.CharsetKeyword.Text))));
            }

            var attributeArguments = CommonConversions.CreateAttributeArgumentList(importAttributes.ToArray());
            var dllImportAttributeList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(dllImportAttributeName, attributeArguments)));

            var attributeLists = (await CommonConversions.ConvertAttributes(node.AttributeLists)).Add(dllImportAttributeList);

            var modifiers = CommonConversions.ConvertModifiers(node, node.Modifiers).Add(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)).Add(SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExternKeyword));
            var returnType = (TypeSyntax) await (node.AsClause?.Type).AcceptAsync(_triviaConvertingExpressionVisitor) ?? SyntaxFactory.ParseTypeName("void");
            var parameterListSyntax = (ParameterListSyntax) await (node.ParameterList).AcceptAsync(_triviaConvertingExpressionVisitor) ??
                                      SyntaxFactory.ParameterList();

            return SyntaxFactory.MethodDeclaration(attributeLists, modifiers, returnType, null, CommonConversions.ConvertIdentifier(node.Identifier), null,
                parameterListSyntax, SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(), null, null).WithSemicolonToken(SemicolonToken);
        }

        public override async Task<CSharpSyntaxNode> VisitTypeParameterList(VBSyntax.TypeParameterListSyntax node)
        {
            return SyntaxFactory.TypeParameterList(
                SyntaxFactory.SeparatedList(await node.Parameters.SelectAsync(async p => (TypeParameterSyntax) await p.AcceptAsync(TriviaConvertingDeclarationVisitor)))
            );
        }

        #endregion


        private async Task<(TypeParameterListSyntax parameters, SyntaxList<TypeParameterConstraintClauseSyntax> constraints)> SplitTypeParameters(VBSyntax.TypeParameterListSyntax typeParameterList)
        {
            var constraints = SyntaxFactory.List<TypeParameterConstraintClauseSyntax>();
            if (typeParameterList == null) return (null, constraints);

            var paramList = new List<TypeParameterSyntax>();
            var constraintList = new List<TypeParameterConstraintClauseSyntax>();
            foreach (var p in typeParameterList.Parameters) {
                var tp = (TypeParameterSyntax) await p.AcceptAsync(TriviaConvertingDeclarationVisitor);
                paramList.Add(tp);
                var constraint = (TypeParameterConstraintClauseSyntax) await (p.TypeParameterConstraintClause).AcceptAsync(TriviaConvertingDeclarationVisitor);
                if (constraint != null)
                    constraintList.Add(constraint);
            }
            var parameters = SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(paramList));
            constraints = SyntaxFactory.List(constraintList);
            return (parameters, constraints);
        }

        public override async Task<CSharpSyntaxNode> VisitTypeParameter(VBSyntax.TypeParameterSyntax node)
        {
            SyntaxToken variance = default(SyntaxToken);
            if (!SyntaxTokenExtensions.IsKind(node.VarianceKeyword, VBasic.SyntaxKind.None)) {
                variance = SyntaxFactory.Token(SyntaxTokenExtensions.IsKind(node.VarianceKeyword, VBasic.SyntaxKind.InKeyword) ? Microsoft.CodeAnalysis.CSharp.SyntaxKind.InKeyword : Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword);
            }
            return SyntaxFactory.TypeParameter(SyntaxFactory.List<AttributeListSyntax>(), variance, CommonConversions.ConvertIdentifier(node.Identifier));
        }

        public override async Task<CSharpSyntaxNode> VisitTypeParameterSingleConstraintClause(VBSyntax.TypeParameterSingleConstraintClauseSyntax node)
        {
            var id = SyntaxFactory.IdentifierName(CommonConversions.ConvertIdentifier(((VBSyntax.TypeParameterSyntax)node.Parent).Identifier));
            return SyntaxFactory.TypeParameterConstraintClause(id, SyntaxFactory.SingletonSeparatedList((TypeParameterConstraintSyntax) await node.Constraint.AcceptAsync(TriviaConvertingDeclarationVisitor)));
        }

        public override async Task<CSharpSyntaxNode> VisitTypeParameterMultipleConstraintClause(VBSyntax.TypeParameterMultipleConstraintClauseSyntax node)
        {
            var id = SyntaxFactory.IdentifierName(CommonConversions.ConvertIdentifier(((VBSyntax.TypeParameterSyntax)node.Parent).Identifier));
            var constraints = await node.Constraints.SelectAsync(async c => (TypeParameterConstraintSyntax) await c.AcceptAsync(TriviaConvertingDeclarationVisitor));
            return SyntaxFactory.TypeParameterConstraintClause(id, SyntaxFactory.SeparatedList(constraints.OrderBy(c => c.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstructorConstraint ? 1 : 0)));
        }

        public override async Task<CSharpSyntaxNode> VisitSpecialConstraint(VBSyntax.SpecialConstraintSyntax node)
        {
            if (SyntaxTokenExtensions.IsKind(node.ConstraintKeyword, VBasic.SyntaxKind.NewKeyword))
                return SyntaxFactory.ConstructorConstraint();
            return SyntaxFactory.ClassOrStructConstraint(node.IsKind(VBasic.SyntaxKind.ClassConstraint) ? Microsoft.CodeAnalysis.CSharp.SyntaxKind.ClassConstraint : Microsoft.CodeAnalysis.CSharp.SyntaxKind.StructConstraint);
        }

        public override async Task<CSharpSyntaxNode> VisitTypeConstraint(VBSyntax.TypeConstraintSyntax node)
        {
            return SyntaxFactory.TypeConstraint((TypeSyntax) await node.Type.AcceptAsync(_triviaConvertingExpressionVisitor));
        }

    }
}
