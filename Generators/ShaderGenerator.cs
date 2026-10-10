using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace HyprNetShell.Generators;

[Generator]
public sealed class ShaderGenerator : IIncrementalGenerator
{
    private const string SHADER_ATTRIBUTE = "HyprNetShell.Rendering.ShaderAttribute";
    private const string UNIFORM_ATTRIBUTE = "HyprNetShell.Rendering.UniformAttribute";
    private static readonly DiagnosticDescriptor DeclarationError = new("HNGL001", "Invalid shader declaration", "{0}", "Shaders", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor FileError = new("HNGL002", "Invalid shader file", "{0}", "Shaders", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UniformError = new("HNGL003", "Invalid uniform mapping", "{0}", "Shaders", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classes = context.SyntaxProvider.ForAttributeWithMetadataName(SHADER_ATTRIBUTE,
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
        var files = context.AdditionalTextsProvider.Where(static file => file.Path.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellation) => (Path: file.Path.Replace('\\', '/'), Text: file.GetText(cancellation)?.ToString())).Collect();
        context.RegisterSourceOutput(classes.Combine(files), static (output, pair) => Generate(output, pair.Left, pair.Right));
        var mappings = context.SyntaxProvider.ForAttributeWithMetadataName(UNIFORM_ATTRIBUTE,
            static (node, _) => node is PropertyDeclarationSyntax,
            static (syntax, _) => (IPropertySymbol)syntax.TargetSymbol);
        context.RegisterSourceOutput(mappings, static (output, property) =>
        {
            if (!property.ContainingType.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == SHADER_ATTRIBUTE))
            {
                output.ReportDiagnostic(Diagnostic.Create(DeclarationError, property.Locations.FirstOrDefault(), "Uniform properties must belong directly to a Shader-attributed class."));
            }
        });
    }

    private static void Generate(SourceProductionContext context, INamedTypeSymbol type, ImmutableArray<(string Path, string? Text)> files)
    {
        var location = type.Locations.FirstOrDefault();
        if (type.IsStatic || type.IsAbstract || type.Arity != 0 || type.ContainingType != null || type.BaseType?.SpecialType != SpecialType.System_Object ||
            type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is not ClassDeclarationSyntax declaration || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            context.ReportDiagnostic(Diagnostic.Create(DeclarationError, location, "Shader must be a top-level, non-generic, non-static partial class with no base class."));
            return;
        }

        if (type.InstanceConstructors.Any(constructor => !constructor.IsImplicitlyDeclared) ||
            type.GetMembers().Any(member => member.Name is "Id" or "Gl" or "Bind" or "Dispose" or "ThrowIfDisposed"))
        {
            context.ReportDiagnostic(Diagnostic.Create(DeclarationError, location, "Shader constructors and Id/Gl/Bind/Dispose/ThrowIfDisposed members are reserved for generated ownership."));
            return;
        }

        var attribute = type.GetAttributes().First(item => item.AttributeClass?.ToDisplayString() == SHADER_ATTRIBUTE);
        var sources = new string[2];
        for (var index = 0; index < 2; index++)
        {
            var path = (attribute.ConstructorArguments[index].Value as string)?.Replace('\\', '/');
            var matches = files.Where(file => !string.IsNullOrWhiteSpace(path) && (file.Path == path || file.Path.EndsWith("/" + path, StringComparison.Ordinal))).ToArray();
            if (matches.Length != 1 || matches[0].Text == null)
            {
                context.ReportDiagnostic(Diagnostic.Create(FileError, location, $"Shader file '{path}' must match exactly one readable GLSL AdditionalFile."));
                return;
            }

            sources[index] = matches[0].Text!;
        }

        var uniforms = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        var capacities = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var stripped = Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
            var constants = Regex.Matches(stripped, @"\bconst\s+int\s+(\w+)\s*=\s*([0-9]+)\s*;")
                .Cast<Match>().GroupBy(match => match.Groups[1].Value)
                .ToDictionary(group => group.Key, group => group.First().Groups[2].Value);
            foreach (Match match in Regex.Matches(stripped, @"\buniform\s+(\w+)\s+(\w+)\s*(\[[^\]]+\])?\s*;"))
            {
                var uniformType = match.Groups[1].Value + (match.Groups[3].Success ? "[]" : "");
                var name = match.Groups[2].Value;
                var capacity = 0;
                if (match.Groups[3].Success)
                {
                    var size = match.Groups[3].Value.Trim('[', ']', ' ', '\t', '\r', '\n');
                    if (constants.TryGetValue(size, out var constant))
                    {
                        size = constant;
                    }

                    if (!int.TryParse(size, out capacity) || capacity <= 0)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(UniformError, location, $"Uniform '{name}' requires a positive fixed array size (integer literal or const int literal)."));
                        return;
                    }
                }

                if (uniforms.TryGetValue(name, out var existing) && (existing != uniformType || capacities[name] != capacity))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UniformError, location, $"Uniform '{name}' has conflicting stage types."));
                    return;
                }

                uniforms[name] = uniformType;
                capacities[name] = capacity;
            }
        }

        var fields = new StringBuilder();
        var properties = new StringBuilder();
        var initialization = new StringBuilder();
        var mapped = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            var mapping = property.GetAttributes().FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == UNIFORM_ATTRIBUTE);
            if (mapping == null)
            {
                continue;
            }

            var name = mapping.ConstructorArguments[0].Value as string ?? "";
            var syntax = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
            var array = property.Type as IArrayTypeSymbol;
            var clr = (array?.ElementType ?? property.Type).ToDisplayString();
            var expected = clr switch {
                "float" => "float",
                "int" => "int",
                "bool" => "bool",
                "System.Numerics.Vector2" => "vec2",
                "System.Numerics.Vector3" => "vec3",
                "System.Numerics.Vector4" => "vec4",
                _ => ""
            };
            var isArray = array != null;
            if (isArray)
            {
                expected = array!.Rank == 1 && (expected == "float" || expected.StartsWith("vec", StringComparison.Ordinal)) ? expected + "[]" : "";
            }

            if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.Private) ||
                type.GetMembers(property.Name + "Location").Length != 0 || type.GetMembers("_location" + property.Name).Length != 0 ||
                syntax == null || !syntax.Modifiers.Any(SyntaxKind.PartialKeyword) ||
                syntax.AccessorList?.Accessors.Count != 2 || syntax.AccessorList.Accessors.Any(accessor => accessor.Body != null || accessor.ExpressionBody != null || accessor.Modifiers.Count != 0) ||
                property.GetMethod == null || property.SetMethod == null || property.SetMethod.IsInitOnly ||
                !uniforms.TryGetValue(name, out var glsl) || expected == "" || (glsl != expected && !(!isArray && clr == "int" && glsl == "sampler2D")) || !mapped.Add(name))
            {
                context.ReportDiagnostic(Diagnostic.Create(UniformError, property.Locations.FirstOrDefault(), $"'{property.Name}' must be an instance partial get/set property with a supported type matching unique GLSL uniform '{name}'."));
                return;
            }

            var identifier = "@" + property.Name;
            var field = "_location" + property.Name;
            if (isArray)
            {
                GenerateArray(property, name, expected, capacities[name], fields, initialization, properties);
                continue;
            }

            fields.AppendLine($"    private readonly int {field};");
            initialization.AppendLine($"            {field} = gl.GetUniformLocation(Id, {Literal(name)});");
            var access = property.DeclaredAccessibility == Accessibility.Public ? "public" : property.DeclaredAccessibility == Accessibility.Internal ? "internal" : "private";
            var fullType = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var components = expected == "vec2" ? 2 : expected == "vec3" ? 3 : expected == "vec4" ? 4 : 1;
            var readType = clr == "int" || clr == "bool" ? "int" : "float";
            var result = clr == "bool" ? "values[0] != 0" : components == 1 ? "values[0]" : $"new {fullType}({string.Join(", ", Enumerable.Range(0, components).Select(i => $"values[{i}]"))})";
            var arguments = clr == "bool" ? "value ? 1 : 0" : components == 1 ? "value" : string.Join(", ", new[] { "X", "Y", "Z", "W" }.Take(components).Select(component => "value." + component));
            properties.AppendLine($"    internal int {property.Name}Location => {field};");
            properties.AppendLine($"    {access} partial {fullType} {identifier}\n    {{\n        get\n        {{\n            ThrowIfDisposed();\n            {readType}* values = stackalloc {readType}[{components}];\n            for (var i = 0; i < {components}; i++)\n            {{\n                values[i] = 0;\n            }}\n\n            if ({field} >= 0)\n            {{\n                Gl.GetUniform(Id, {field}, values);\n            }}\n\n            return {result};\n        }}\n\n        set\n        {{\n            ThrowIfDisposed();\n            if ({field} < 0)\n            {{\n                return;\n            }}\n\n            var previous = Gl.GetInteger(global::Silk.NET.OpenGL.GetPName.CurrentProgram);\n            var bindingChanged = (uint)previous != Id;\n            if (bindingChanged)\n            {{\n                Gl.UseProgram(Id);\n            }}\n\n            try\n            {{\n                Gl.Uniform{components}({field}, {arguments});\n            }}\n\n            finally\n            {{\n                if (bindingChanged)\n                {{\n                    Gl.UseProgram((uint)previous);\n                }}\n            }}\n        }}\n    }}\n");
        }

        var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : $"namespace {type.ContainingNamespace.ToDisplayString()};\n";
        var code = $"// <auto-generated/>\n#nullable enable\n{ns}\nunsafe partial class @{type.Name} : global::HyprNetShell.Rendering.ShaderProgram\n{{\n{fields}\n{properties}\n    public @{type.Name}(global::Silk.NET.OpenGL.GL gl)\n        : base(gl, {Literal(sources[0])}, {Literal(sources[1])}, {Literal(type.Name)})\n    {{\n        try\n        {{\n{initialization}        }}\n\n        catch\n        {{\n            Dispose();\n            throw;\n        }}\n    }}\n}}\n";
        context.AddSource(type.ToDisplayString().Replace('.', '_') + ".Shader.g.cs", SourceText.From(code, Encoding.UTF8));
    }

    private static void GenerateArray(IPropertySymbol property, string name, string expected, int capacity,
        StringBuilder fields, StringBuilder initialization, StringBuilder properties)
    {
        var field = "_location" + property.Name;
        var element = ((IArrayTypeSymbol)property.Type).ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var fullType = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var access = property.DeclaredAccessibility == Accessibility.Public ? "public" : property.DeclaredAccessibility == Accessibility.Internal ? "internal" : "private";
        var components = expected == "vec2[]" ? 2 : expected == "vec3[]" ? 3 : expected == "vec4[]" ? 4 : 1;
        var result = components == 1 ? "values[0]" : $"new {element}({string.Join(", ", Enumerable.Range(0, components).Select(i => $"values[{i}]"))})";
        fields.AppendLine($"    private readonly int[] {field};");
        initialization.AppendLine($"            {field} = new int[{capacity}];\n            for (var i = 0; i < {capacity}; i++)\n            {{\n                {field}[i] = gl.GetUniformLocation(Id, {Literal(name + "[")} + i.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + \"]\");\n            }}\n");
        properties.AppendLine($"    internal int {property.Name}Location => {field}[0];");
        properties.AppendLine($"    {access} partial {fullType} @{property.Name}\n    {{\n        get\n        {{\n            ThrowIfDisposed();\n            var result = new {element}[{capacity}];\n            float* values = stackalloc float[{components}];\n            for (var i = 0; i < result.Length; i++)\n            {{\n                if ({field}[i] < 0)\n                {{\n                    continue;\n                }}\n\n                Gl.GetUniform(Id, {field}[i], values);\n                result[i] = {result};\n            }}\n\n            return result;\n        }}\n\n        set\n        {{\n            ThrowIfDisposed();\n            global::System.ArgumentNullException.ThrowIfNull(value);\n            if (value.Length > {capacity})\n            {{\n                throw new global::System.ArgumentException(\"Uniform array exceeds its declared capacity of {capacity}.\", nameof(value));\n            }}\n\n            if (value.Length == 0 || {field}[0] < 0)\n            {{\n                return;\n            }}\n\n            var previous = Gl.GetInteger(global::Silk.NET.OpenGL.GetPName.CurrentProgram);\n            var bindingChanged = (uint)previous != Id;\n            if (bindingChanged)\n            {{\n                Gl.UseProgram(Id);\n            }}\n\n            try\n            {{\n                fixed ({element}* data = value)\n                {{\n                    Gl.Uniform{components}({field}[0], (uint)value.Length, (float*)data);\n                }}\n            }}\n\n            finally\n            {{\n                if (bindingChanged)\n                {{\n                    Gl.UseProgram((uint)previous);\n                }}\n            }}\n        }}\n    }}\n");
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
}
