﻿using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Moonlight.Generators.Models;
using Moonlight.Generators.Problems;
using Moonlight.Generators.Serialization;
using Moonlight.Generators.Syntax;

namespace Moonlight.Generators
{
    public class SerializationEngine : ISyntaxContextReceiver
    {
        private static string Notice => "// Auto-generated by the Serialization Generator.";
        private const string EnumerableQualifiedName = "System.Collections.Generic.IEnumerable`1";
        private const string PackingMethod = "PackSerializedBytes";
        private const string UnpackingMethod = "UnpackSerializedBytes";

        private static readonly Dictionary<string, string[]> DeconstructionTypes = new()
        {
            ["System.Collections.Generic.KeyValuePair`2"] = new[] { "Key", "Value" },
            ["System.Tuple`2"] = new[] { "Item1", "Item2" }
        };

        private static readonly Dictionary<string, IDefaultSerialization> DefaultSerialization = new()
        {
            ["System.Collections.Generic.KeyValuePair`2"] = new KeyValuePairSerialization(),
            ["System.DateTime"] = new DateTimeSerialization(),
            ["System.Tuple`1"] = new TupleSingleSerialization(),
            ["System.Tuple`2"] = new TupleDoubleSerialization(),
            ["System.Tuple`3"] = new TupleTripleSerialization(),
            ["System.Tuple`4"] = new TupleQuadrupleSerialization(),
            ["System.Tuple`5"] = new TupleQuintupleSerialization(),
            ["System.Tuple`6"] = new TupleSextupleSerialization(),
            ["System.Tuple`7"] = new TupleSeptupleSerialization(),
        };

        private static readonly Dictionary<string, string> PredefinedTypes = new()
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
            var defaultUsingDeclarations = new Dictionary<string, bool>
            {
                ["System"] = false,
                ["System.IO"] = false,
                ["System.Linq"] = false
            };

            foreach (var usingDecl in item.Unit.Usings)
            {
                if (defaultUsingDeclarations.ContainsKey(usingDecl.Name.ToString()))
                {
                    defaultUsingDeclarations[usingDecl.Name.ToString()] = true;
                }

                code.AppendLine($"using {usingDecl.Name};");
            }

            foreach (var defaultUsing in defaultUsingDeclarations.Where(defaultUsing => !defaultUsing.Value))
            {
                code.AppendLine($"using {defaultUsing.Key};");
            }

            code.AppendLine();

            var properties = new List<IPropertySymbol>();
            var shouldOverride =
                symbol.BaseType != null && symbol.BaseType.GetAttributes()
                    .Any(self => self.AttributeClass is { Name: "SerializationAttribute" });

            foreach (var member in GetAllMembers(symbol))
            {
                if (member is not IPropertySymbol propertySymbol) continue;
                if (propertySymbol.GetAttributes()
                    .Any(self => self.AttributeClass is { Name: "IgnoreAttribute" })) continue;
                if (propertySymbol.DeclaredAccessibility != Accessibility.Public || propertySymbol.IsIndexer ||
                    propertySymbol.IsReadOnly || propertySymbol.IsWriteOnly) continue;

                properties.Add(propertySymbol);
            }

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

                            foreach (var property in properties)
                            {
                                code.AppendLine();
                                code.AppendLine($"// Property: {property.Name} ({property.Type.MetadataName})");

                                using (code.BeginScope())
                                {
                                    AppendWriteLogic(property, property.Type, code, property.Name,
                                        symbol.Locations.FirstOrDefault());
                                }
                            }
                        }
                    }

                    if (!HasImplementation(symbol, UnpackingMethod))
                    {
                        using (code.BeginScope(
                            $"public {(shouldOverride ? "new " : string.Empty)}void {UnpackingMethod}(BinaryReader reader)"))
                        {
                            code.AppendLine(Notice);

                            foreach (var property in properties)
                            {
                                code.AppendLine();
                                code.AppendLine($"// Property: {property.Name} ({property.Type.MetadataName})");

                                using (code.BeginScope())
                                {
                                    AppendReadLogic(property, property.Type, code, property.Name,
                                        symbol.Locations.FirstOrDefault());
                                }
                            }
                        }
                    }
                }
            }

            return code;
        }

        public void AppendWriteLogic(IPropertySymbol property, ITypeSymbol type, CodeWriter code, string name,
            Location location)
        {
            var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;

            if (nullable)
            {
                type = ((INamedTypeSymbol) type).TypeArguments.First();

                code.AppendLine($"writer.Write({name}.HasValue);");
                code.AppendLine($"if ({name}.HasValue)");
            }

            name = nullable ? $"{name}.Value" : name;

            if (DefaultSerialization.TryGetValue(GetQualifiedName(type), out var serialization))
            {
                serialization.Serialize(this, property, type, code, name,
                    GetIdentifierWithArguments((INamedTypeSymbol) type), location);

                return;
            }

            if (IsPrimitive(type))
            {
                code.AppendLine($"writer.Write({name});");
            }
            else
            {
                switch (type.TypeKind)
                {
                    case TypeKind.Enum:
                        code.AppendLine($"writer.Write((int) {name});");

                        break;
                    case TypeKind.Interface:
                    case TypeKind.Struct:
                    case TypeKind.Class:
                        var enumerable = GetQualifiedName(type) == EnumerableQualifiedName
                            ? (INamedTypeSymbol) type
                            : type.AllInterfaces.FirstOrDefault(self =>
                                GetQualifiedName(self) == EnumerableQualifiedName);

                        if (type.TypeKind == TypeKind.Class && !nullable)
                        {
                            code.AppendLine($"writer.Write({name} != null);");
                            code.AppendLine($"if ({name} != null)");
                        }

                        if (enumerable != null)
                        {
                            var elementType = enumerable.TypeArguments.First();

                            using (code.BeginScope())
                            {
                                var countTechnique = GetAllMembers(type)
                                    .Where(member => member is IPropertySymbol)
                                    .Aggregate("Count()", (current, symbol) => symbol.Name switch
                                    {
                                        "Count" => "Count",
                                        "Length" => "Length",
                                        _ => current
                                    });

                                code.AppendLine($"var count = {name}.{countTechnique};");
                                code.AppendLine("writer.Write(count);");

                                using (code.BeginScope($"foreach (var entry in {name})"))
                                {
                                    AppendWriteLogic(property, elementType, code, "entry", location);
                                }
                            }
                        }
                        else
                        {
                            if (type.TypeKind == TypeKind.Interface)
                            {
                                var problem = new SerializationProblem
                                {
                                    Descriptor = new DiagnosticDescriptor(ProblemId.InterfaceProperties,
                                        "Interface Properties",
                                        "Could not serialize property '{0}' of type {1} because Interface types are not supported",
                                        "serialization",
                                        DiagnosticSeverity.Error, true),
                                    Locations = new[] { property.Locations.FirstOrDefault(), location },
                                    Format = new object[] { property.Name, type.Name }
                                };

                                Problems.Add(problem);

                                code.AppendLine(
                                    $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");

                                return;
                            }

                            if (HasImplementation(type, PackingMethod) || HasMarkedAsSerializable(type))
                            {
                                code.AppendLine($"{name}.{PackingMethod}(writer);");
                            }
                            else
                            {
                                var problem = new SerializationProblem
                                {
                                    Descriptor = new DiagnosticDescriptor(ProblemId.MissingPackingMethod,
                                        "Packing Method",
                                        "Could not serialize property '{0}' because {1} is missing method {2}",
                                        "serialization",
                                        DiagnosticSeverity.Error, true),
                                    Locations = new[] { property.Locations.FirstOrDefault(), location },
                                    Format = new object[] { property.Name, type.Name, PackingMethod }
                                };

                                Problems.Add(problem);

                                code.AppendLine(
                                    $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");
                            }
                        }

                        break;
                    case TypeKind.Array:
                        var array = (IArrayTypeSymbol) type;

                        code.AppendLine($"writer.Write({name}.Length);");

                        using (code.BeginScope($"for (var idx = 0; idx < {name}.Length; idx++)"))
                        {
                            AppendWriteLogic(property, array.ElementType, code, $"{name}[idx]", location);
                        }


                        break;
                }
            }
        }

        public void AppendReadLogic(IPropertySymbol property, ITypeSymbol type, CodeWriter code, string name,
            Location location)
        {
            var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;

            if (nullable)
            {
                type = ((INamedTypeSymbol) type).TypeArguments.First();
                code.AppendLine("if (reader.ReadBoolean())");
            }

            if (DefaultSerialization.TryGetValue(GetQualifiedName(type), out var serialization))
            {
                serialization.Deserialize(this, property, type, code, name,
                    GetIdentifierWithArguments((INamedTypeSymbol) type), location);

                return;
            }

            if (IsPrimitive(type))
            {
                code.AppendLine(
                    $"{name} = reader.Read{(PredefinedTypes.TryGetValue(type.Name, out var result) ? result : type.Name)}();");
            }
            else
            {
                switch (type.TypeKind)
                {
                    case TypeKind.Enum:
                        code.AppendLine($"{name} = ({type.Name}) reader.ReadInt32();");

                        break;
                    case TypeKind.Interface:
                    case TypeKind.Struct:
                    case TypeKind.Class:
                        var enumerable = GetQualifiedName(type) == EnumerableQualifiedName
                            ? (INamedTypeSymbol) type
                            : type.AllInterfaces.FirstOrDefault(self =>
                                GetQualifiedName(self) == EnumerableQualifiedName);

                        if (type.TypeKind == TypeKind.Class && !nullable)
                        {
                            code.AppendLine("if (reader.ReadBoolean())");
                        }

                        if (enumerable != null)
                        {
                            var elementType = (INamedTypeSymbol) enumerable.TypeArguments.First();

                            if (type.TypeKind == TypeKind.Interface &&
                                GetQualifiedName(type) != EnumerableQualifiedName)
                            {
                                var problem = new SerializationProblem
                                {
                                    Descriptor = new DiagnosticDescriptor(ProblemId.InterfaceProperties,
                                        "Interface Properties",
                                        "Could not deserialize property '{0}' of type {1} because Interface types are not supported",
                                        "serialization",
                                        DiagnosticSeverity.Error, true),
                                    Locations = new[] { property.Locations.FirstOrDefault(), location },
                                    Format = new object[] { property.Name, type.Name }
                                };

                                Problems.Add(problem);

                                code.AppendLine(
                                    $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");

                                return;
                            }

                            using (code.BeginScope())
                            {
                                code.AppendLine("var count = reader.ReadInt32();");

                                var constructor =
                                    ((INamedTypeSymbol) type).Constructors.FirstOrDefault(
                                        self => GetQualifiedName(self.Parameters.FirstOrDefault()?.Type) ==
                                                EnumerableQualifiedName);

                                var method = HasImplementation(type, "Add", GetQualifiedName(elementType));
                                var deconstructed = false;

                                if (DeconstructionTypes.ContainsKey(GetQualifiedName(elementType)))
                                {
                                    deconstructed = HasImplementation(type, "Add",
                                        elementType.TypeArguments.Cast<INamedTypeSymbol>().Select(GetQualifiedName)
                                            .ToArray());
                                }

                                if (method || deconstructed)
                                {
                                    code.AppendLine(
                                        $"{name} = new {GetIdentifierWithArguments((INamedTypeSymbol) type)}();");
                                }
                                else
                                {
                                    code.AppendLine(
                                        $"var temp = new {GetIdentifierWithArguments(elementType)}[count];");
                                }

                                using (code.BeginScope("for (var idx = 0; idx < count; idx++)"))
                                {
                                    AppendReadLogic(property, elementType, code,
                                        method || deconstructed ? "var transient" : "temp[idx]", location);

                                    if (method)
                                    {
                                        code.AppendLine($"{name}.Add(transient);");
                                    }
                                    else if (deconstructed)
                                    {
                                        var arguments = DeconstructionTypes[GetQualifiedName(elementType)]
                                            .Select(self => $"transient.{self}");

                                        code.AppendLine($"{name}.Add({string.Join(",", arguments)});");
                                    }
                                }

                                if (method || deconstructed)
                                {
                                    return;
                                }

                                if (constructor != null)
                                {
                                    code.AppendLine($"{name} = new {GetIdentifierWithArguments(enumerable)}(temp);");

                                    return;
                                }

                                if (GetQualifiedName(type) != EnumerableQualifiedName)
                                {
                                    var problem = new SerializationProblem
                                    {
                                        Descriptor = new DiagnosticDescriptor(ProblemId.EnumerableProperties,
                                            "Enumerable Properties",
                                            "Could not deserialize property '{0}' because enumerable type {1} did not contain a suitable way of adding items",
                                            "serialization",
                                            DiagnosticSeverity.Error, true),
                                        Locations = new[] { property.Locations.FirstOrDefault(), location },
                                        Format = new object[] { property.Name, type.Name, elementType.Name }
                                    };

                                    Problems.Add(problem);

                                    code.AppendLine(
                                        $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");

                                    return;
                                }

                                code.AppendLine($"{name} = temp;");
                            }
                        }
                        else
                        {
                            if (type.TypeKind == TypeKind.Interface)
                            {
                                var problem = new SerializationProblem
                                {
                                    Descriptor = new DiagnosticDescriptor(ProblemId.InterfaceProperties,
                                        "Interface Properties",
                                        "Could not deserialize property '{0}' of type {1} because Interface types are not supported",
                                        "serialization",
                                        DiagnosticSeverity.Error, true),
                                    Locations = new[] { property.Locations.FirstOrDefault(), location },
                                    Format = new object[] { property.Name, type.Name }
                                };

                                Problems.Add(problem);

                                code.AppendLine(
                                    $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");

                                return;
                            }

                            code.AppendLine(
                                $"{name} = new {GetIdentifierWithArguments((INamedTypeSymbol) type)}(reader);");
                        }

                        break;
                    case TypeKind.Array:
                        var array = (IArrayTypeSymbol) type;

                        using (code.BeginScope())
                        {
                            code.AppendLine("var length = reader.ReadInt32();");
                            code.AppendLine($"{name} = new {array.ElementType}[length];");

                            using (code.BeginScope("for (var idx = 0; idx < length; idx++)"))
                            {
                                AppendReadLogic(property, array.ElementType, code, $"{name}[idx]", location);
                            }
                        }

                        break;
                }
            }
        }

        private static bool IsPrimitive(ITypeSymbol type)
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

        private static string GetIdentifierWithArguments(INamedTypeSymbol symbol)
        {
            var builder = new StringBuilder();

            builder.Append(symbol.Name);

            if (symbol.TypeArguments == null || symbol.TypeArguments.IsDefaultOrEmpty) return builder.ToString();

            builder.Append("<");
            builder.Append(string.Join(",",
                symbol.TypeArguments.Cast<INamedTypeSymbol>().Select(GetIdentifierWithArguments)));
            builder.Append(">");

            return builder.ToString();
        }

        public static string GetQualifiedName(ISymbol symbol)
        {
            return symbol != null ? $"{symbol.ContainingNamespace}.{symbol.MetadataName}" : null;
        }

        private static bool HasMarkedAsSerializable(ISymbol symbol)
        {
            var attribute = symbol?.GetAttributes()
                .FirstOrDefault(self => self.AttributeClass is { Name: "SerializationAttribute" });

            return attribute != null;
        }

        private bool HasImplementation(ITypeSymbol symbol, string methodName,
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

        private static IEnumerable<ISymbol> GetAllMembers(ITypeSymbol symbol)
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

            return members;
        }
    }
}