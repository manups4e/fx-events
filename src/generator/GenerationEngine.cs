﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lusive.Events.Generator.Generation;
using Lusive.Events.Generator.Models;
using Lusive.Events.Generator.Problems;
using Lusive.Events.Generator.Serialization;
using Lusive.Events.Generator.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lusive.Events.Generator
{
    public class GenerationEngine : ISyntaxContextReceiver
    {
        public static GenerationEngine Instance { get; } = new();

        private static string Notice => "// Auto-generated by the Serialization Generator.";
        public const string EnumerableQualifiedName = "System.Collections.Generic.IEnumerable`1";
        public const string PackingMethod = "PackSerializedBytes";
        public const string UnpackingMethod = "UnpackSerializedBytes";

        public static readonly Dictionary<string, string[]> DeconstructionTypes = new()
        {
            ["System.Collections.Generic.KeyValuePair`2"] = new[] { "Key", "Value" },
            ["System.Tuple`2"] = new[] { "Item1", "Item2" }
        };

        public static readonly Dictionary<string, IDefaultSerialization> DefaultSerialization = new()
        {
            ["System.Collections.Generic.KeyValuePair`2"] = new KeyValuePairSerialization(),
            ["System.DateTime"] = new DateTimeSerialization(),
            ["System.TimeSpan"] = new TimeSpanSerialization(),
            ["System.Tuple`1"] = new TupleSingleSerialization(),
            ["System.Tuple`2"] = new TupleDoubleSerialization(),
            ["System.Tuple`3"] = new TupleTripleSerialization(),
            ["System.Tuple`4"] = new TupleQuadrupleSerialization(),
            ["System.Tuple`5"] = new TupleQuintupleSerialization(),
            ["System.Tuple`6"] = new TupleSextupleSerialization(),
            ["System.Tuple`7"] = new TupleSeptupleSerialization()
        };

        public static readonly Dictionary<string, string> PredefinedTypes = new()
        {
            { "bool", "Bool" },
            { "byte", "Byte" },
            { "byte[]", "Bytes" },
            { "char", "Char" },
            { "char[]", "Chars" },
            { "decimal", "Decimal" },
            { "double", "Double" },
            { "short", "Int16" },
            { "int", "Int32" },
            { "long", "Int64" },
            { "float", "Single" },
            { "string", "String" },
            { "sbyte", "SByte" },
            { "ushort", "UInt16" },
            { "uint", "UInt32" },
            { "ulong", "UInt64" }
        };

        public readonly List<WorkItem> WorkItems = new();
        public readonly List<SerializationProblem> Problems = new();
        public readonly List<string> Logs = new();

        private GenerationEngine()
        {
        }

        public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
        {
            if (context.Node is not ClassDeclarationSyntax classDecl) return;

            var symbol = (INamedTypeSymbol) context.SemanticModel.GetDeclaredSymbol(context.Node);

            if (symbol == null) return;
            if (!HasMarkedAsSerializable(symbol)) return;

            var hasPartial = classDecl.Modifiers.Any(self => self.ToString() == "partial");

            if (!hasPartial)
            {
                var problem = new SerializationProblem
                {
                    Descriptor = new DiagnosticDescriptor(ProblemId.SerializationMarking, "Serialization Marking",
                        "Serialization marked type {0} is missing the partial keyword.", "serialization",
                        DiagnosticSeverity.Error, true),
                    Locations = new[] { symbol.Locations.FirstOrDefault() },
                    Format = new object[] { symbol.Name }
                };

                Problems.Add(problem);

                return;
            }

            CompilationUnitSyntax unit = null;
            NamespaceDeclarationSyntax namespaceDecl = null;
            SyntaxNode parent = classDecl;

            while ((parent = parent.Parent) != null)
            {
                switch (parent)
                {
                    case CompilationUnitSyntax syntax:
                        unit = syntax;

                        break;
                    case NamespaceDeclarationSyntax syntax:
                        namespaceDecl = syntax;

                        break;
                }
            }

            if (unit == null || namespaceDecl == null) return;

            WorkItems.Add(new WorkItem
            {
                TypeSymbol = symbol, SemanticModel = context.SemanticModel, ClassDeclaration = classDecl, Unit = unit,
                NamespaceDeclaration = namespaceDecl
            });
        }

        public CodeWriter Compile(WorkItem item)
        {
            var symbol = item.TypeSymbol;
            var code = new CodeWriter();
            var imports = new Dictionary<string, bool>
            {
                ["System"] = true, ["System.IO"] = true, ["System.Linq"] = true,
            };

            foreach (var usingDecl in item.Unit.Usings)
            {
                imports[usingDecl.Name.ToString()] = true;
            }

            foreach (var import in imports.Where(import => import.Value))
            {
                code.AppendLine($"using {import.Key};");
            }

            code.AppendLine();

            var shouldOverride =
                symbol.BaseType != null && symbol.BaseType.GetAttributes()
                    .Any(self => self.AttributeClass is { Name: "SerializationAttribute" });


            using (code.BeginScope($"namespace {item.NamespaceDeclaration.Name}"))
            {
                using (code.BeginScope(
                    $"public partial class {item.ClassDeclaration.Identifier}{item.ClassDeclaration.TypeParameterList} {item.ClassDeclaration.ConstraintClauses}"))
                {
                    if (!item.ClassDeclaration.DescendantNodes().Any(self =>
                        self is ConstructorDeclarationSyntax constructorDecl &&
                        constructorDecl.ParameterList.Parameters.Count == 0))
                    {
                        using (code.BeginScope($"public {symbol.Name}()"))
                        {
                        }
                    }

                    using (code.BeginScope($"public {symbol.Name}(BinaryReader reader)"))
                    {
                        code.AppendLine($"{UnpackingMethod}(reader);");
                    }

                    if (!HasImplementation(symbol, PackingMethod))
                    {
                        using (code.BeginScope(
                            $"public {(shouldOverride ? "new " : string.Empty)}void {PackingMethod}(BinaryWriter writer)"))
                        {
                            code.AppendLine(Notice);

                            Generate(string.Empty, symbol, code, GenerationType.Write);
                        }
                    }

                    if (!HasImplementation(symbol, UnpackingMethod))
                    {
                        using (code.BeginScope(
                            $"public {(shouldOverride ? "new " : string.Empty)}void {UnpackingMethod}(BinaryReader reader)"))
                        {
                            code.AppendLine(Notice);

                            Generate(string.Empty, symbol, code, GenerationType.Read);
                        }
                    }
                }
            }

            return code;
        }

        public static IEnumerable<Tuple<ISymbol, ITypeSymbol>> GetMembers(ITypeSymbol symbol)
        {
            var members = new List<Tuple<ISymbol, bool>>();

            foreach (var member in GetAllMembers(symbol))
            {
                if (member is not IPropertySymbol && member is not IFieldSymbol) continue;

                var attributes = member.GetAttributes();

                if (attributes.Any(self => self.AttributeClass is { Name: "IgnoreAttribute" })) continue;

                var forced = attributes.Any(self => self.AttributeClass is { Name: "ForceAttribute" });

                if (!forced && member.DeclaredAccessibility != Accessibility.Public) continue;

                if (member is IPropertySymbol propertySymbol && !forced && (
                    propertySymbol.IsIndexer || propertySymbol.IsReadOnly ||
                    propertySymbol.IsWriteOnly)) continue;

                members.Add(Tuple.Create(member, forced));
            }

            foreach (var (member, forced) in members)
            {
                if (forced && member is IPropertySymbol { IsReadOnly: true }) continue;

                var valueType = member switch
                {
                    IPropertySymbol propertySymbol => propertySymbol.Type,
                    IFieldSymbol fieldSymbol => fieldSymbol.Type,
                    _ => null
                };

                if (valueType == null) continue;

                yield return Tuple.Create(member, valueType);
            }
        }

        public static void Generate(string target, ITypeSymbol symbol, CodeWriter code, GenerationType type)
        {
            foreach (var (member, valueType) in GetMembers(symbol))
            {
                code.AppendLine();
                code.AppendLine($"// Member: {member.Name} ({valueType.MetadataName})");

                using (code.BeginScope())
                {
                    switch (type)
                    {
                        case GenerationType.Read:
                            ReadGenerator.Make(member, valueType, code, target + member.Name,
                                symbol.Locations.FirstOrDefault());

                            break;
                        case GenerationType.Write:
                            WriteGenerator.Make(member, valueType, code, target + member.Name,
                                symbol.Locations.FirstOrDefault());

                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(type), type, null);
                    }
                }
            }
        }

        public static bool IsPrimitive(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_Int32:
                case SpecialType.System_Int64:
                case SpecialType.System_Byte:
                case SpecialType.System_UInt16:
                case SpecialType.System_UInt32:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Char:
                case SpecialType.System_String:
                case SpecialType.System_Object:
                    return true;
                default:
                    return false;
            }
        }

        public static string GetCamelCase(string value)
        {
            if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
                return value;

            return char.ToLower(value[0]) + value.Substring(1);
        }

        public static string GetIdentifierWithArguments(ISymbol symbol)
        {
            var builder = new StringBuilder();

            builder.Append(GetFullName(symbol));

            if (symbol is not INamedTypeSymbol named || named.TypeArguments == null ||
                named.TypeArguments.IsDefaultOrEmpty) return builder.ToString();

            builder.Append("<");
            builder.Append(string.Join(",",
                named.TypeArguments.Cast<INamedTypeSymbol>().Select(GetIdentifierWithArguments)));
            builder.Append(">");

            return builder.ToString();
        }

        public static string GetQualifiedName(ISymbol symbol)
        {
            var name = symbol != null ? GetFullName(symbol) : null;

            if (symbol is not INamedTypeSymbol { TypeArguments: { Length: > 0 } } named) return name;

            name += "`";
            name += named.TypeArguments.Length;

            return name;
        }

        private static string GetFullName(ISymbol symbol)
        {
            var builder = new StringBuilder();
            var containing = symbol;

            builder.Append(symbol.ContainingNamespace);
            builder.Append(".");

            var idx = builder.Length;

            while ((containing = containing.ContainingType) != null)
            {
                builder.Insert(idx, containing.Name + ".");
            }

            builder.Append(symbol.Name);

            return builder.ToString();
        }

        public static bool HasMarkedAsSerializable(ISymbol symbol)
        {
            var attribute = symbol?.GetAttributes()
                .FirstOrDefault(self => self.AttributeClass is { Name: "SerializationAttribute" });

            return attribute != null;
        }

        public static bool HasImplementation(ITypeSymbol symbol, string methodName,
            params string[] parameters)
        {
            foreach (var member in GetAllMembers(symbol))
            {
                if (member is not IMethodSymbol methodSymbol || methodSymbol.Name != methodName) continue;
                if (parameters == null || parameters.Length == 0) return true;

                var failed = false;

                for (var index = 0; index < parameters.Length; index++)
                {
                    var parameter = parameters[index];

                    if (methodSymbol.Parameters.Length == index)
                    {
                        failed = true;
                        break;
                    }

                    if (GetQualifiedName(methodSymbol.Parameters[index].Type) == parameter) continue;

                    failed = true;

                    break;
                }

                if (failed) continue;

                return true;
            }

            return false;
        }

        public static IEnumerable<ISymbol> GetAllMembers(ITypeSymbol symbol)
        {
            var members = new List<ISymbol>();

            members.AddRange(symbol.GetMembers());

            if (symbol.BaseType != null)
                members.AddRange(
                    symbol.BaseType.GetMembers().Where(self => members.All(deep => self.Name != deep.Name)));

            foreach (var type in symbol.AllInterfaces)
            {
                members.AddRange(type.GetMembers().Where(self => members.All(deep => self.Name != deep.Name)));
            }

            return members.Where(self => !self.IsStatic);
        }
    }
}