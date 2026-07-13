using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace HyprNetShell.Generators;

[Generator]
public sealed class SvgAssetGenerator : IIncrementalGenerator
{
    private const string AttributeName = "HyprNetShell.Rendering.SvgAssetAttribute";

    private static readonly DiagnosticDescriptor MissingAsset = new(
        "HNSVG001",
        "SVG asset was not found",
        "SVG asset '{0}' is not included in AdditionalFiles",
        "SvgAssets",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "HNSVG002",
        "Invalid SVG asset declaration",
        "SVG asset property '{0}' must be static, partial, and declared directly inside a partial class",
        "SvgAssets",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
                AttributeName,
                static (node, _) => node is PropertyDeclarationSyntax,
                static (syntaxContext, _) => CreateDeclaration(syntaxContext))
            .Where(static declaration => declaration is not null);

        var svgFiles = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .Collect();

        context.RegisterSourceOutput(
            declarations.Combine(svgFiles),
            static (productionContext, value) =>
                Generate(productionContext, value.Left!, value.Right));
    }

    private static AssetDeclaration? CreateDeclaration(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IPropertySymbol property ||
            context.TargetNode is not PropertyDeclarationSyntax syntax)
        {
            return null;
        }

        var paths = ImmutableArray.CreateBuilder<string>();
        foreach (var argument in context.Attributes[0].ConstructorArguments)
        {
            if (argument.Kind == TypedConstantKind.Array)
            {
                foreach (var item in argument.Values)
                {
                    if (item.Value is string path) paths.Add(path);
                }
            }
            else if (argument.Value is string path)
            {
                paths.Add(path);
            }
        }

        var containingType = property.ContainingType;
        var valid = property.IsStatic &&
                    syntax.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                    syntax.Parent is ClassDeclarationSyntax classSyntax &&
                    classSyntax.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                    containingType.ContainingType is null;

        return new AssetDeclaration(
            containingType.ContainingNamespace.IsGlobalNamespace
                ? null
                : containingType.ContainingNamespace.ToDisplayString(),
            containingType.Name,
            AccessibilityText(containingType.DeclaredAccessibility),
            containingType.IsStatic,
            property.Name,
            AccessibilityText(property.DeclaredAccessibility),
            property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            paths.ToImmutable(),
            valid,
            property.Locations.FirstOrDefault());
    }

    private static void Generate(
        SourceProductionContext context,
        AssetDeclaration declaration,
        ImmutableArray<AdditionalText> files)
    {
        if (!declaration.Valid || declaration.Paths.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidDeclaration,
                declaration.Location,
                declaration.PropertyName));
            return;
        }

        var assets = new List<(string Path, string Base64)>();
        foreach (var requestedPath in declaration.Paths)
        {
            var normalized = NormalizePath(requestedPath);
            var matches = files.Where(file =>
                    NormalizePath(file.Path).EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase) ||
                    NormalizePath(file.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1 || matches[0].GetText(context.CancellationToken) is not SourceText text)
            {
                context.ReportDiagnostic(Diagnostic.Create(MissingAsset, declaration.Location, requestedPath));
                return;
            }

            assets.Add((
                normalized,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(text.ToString()))));
        }

        var backingName = "__svgAsset_" + declaration.PropertyName;
        var expression = assets.Count == 1
            ? AssetExpression(assets[0])
            : "new global::HyprNetShell.Rendering.SvgAsset[] { " +
              string.Join(", ", assets.Select(AssetExpression)) + " }";

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable enable");
        if (declaration.Namespace is not null)
        {
            source.Append("namespace ").Append(declaration.Namespace).AppendLine(";");
            source.AppendLine();
        }

        source.Append(declaration.TypeAccessibility).Append(' ');
        if (declaration.TypeIsStatic) source.Append("static ");
        source.Append("partial class ").Append(declaration.TypeName).AppendLine();
        source.AppendLine("{");
        source.Append("    private static readonly ").Append(declaration.PropertyType).Append(' ')
            .Append(backingName).Append(" = ").Append(expression).AppendLine(";");
        source.AppendLine();
        source.Append("    ").Append(declaration.PropertyAccessibility).Append(" static partial ")
            .Append(declaration.PropertyType).Append(' ').Append(declaration.PropertyName)
            .Append(" => ").Append(backingName).AppendLine(";");
        source.AppendLine("}");

        context.AddSource(
            declaration.TypeName + "." + declaration.PropertyName + ".SvgAsset.g.cs",
            SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static string AssetExpression((string Path, string Base64) asset) =>
        "new global::HyprNetShell.Rendering.SvgAsset(" +
        SymbolDisplay.FormatLiteral(asset.Path, true) + ", " +
        SymbolDisplay.FormatLiteral(asset.Base64, true) + ")";

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string AccessibilityText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "internal",
    };

    private sealed class AssetDeclaration
    {
        public AssetDeclaration(
            string? @namespace,
            string typeName,
            string typeAccessibility,
            bool typeIsStatic,
            string propertyName,
            string propertyAccessibility,
            string propertyType,
            ImmutableArray<string> paths,
            bool valid,
            Location? location)
        {
            Namespace = @namespace;
            TypeName = typeName;
            TypeAccessibility = typeAccessibility;
            TypeIsStatic = typeIsStatic;
            PropertyName = propertyName;
            PropertyAccessibility = propertyAccessibility;
            PropertyType = propertyType;
            Paths = paths;
            Valid = valid;
            Location = location;
        }

        public string? Namespace { get; }
        public string TypeName { get; }
        public string TypeAccessibility { get; }
        public bool TypeIsStatic { get; }
        public string PropertyName { get; }
        public string PropertyAccessibility { get; }
        public string PropertyType { get; }
        public ImmutableArray<string> Paths { get; }
        public bool Valid { get; }
        public Location? Location { get; }
    }
}
