﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Reinterop
{
    [Generator]
    internal class RoslynSourceGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new ReinteropSyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            ReinteropSyntaxReceiver receiver = (ReinteropSyntaxReceiver)context.SyntaxReceiver!;

            CSharpReinteropAttribute.Generate(context);
            CSharpReinteropNativeImplementationAttribute.Generate(context);
            CSharpObjectHandleUtility.Generate(context);

            // Create a new Compilation with the CSharpObjectHandleUtility created above.
            // Newer versions of Roslyn make this easy, but not the one in Unity.
            CSharpParseOptions options = (CSharpParseOptions)((CSharpCompilation)context.Compilation).SyntaxTrees[0].Options;
            Compilation compilation = context.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(SourceText.From(CSharpObjectHandleUtility.Source), options));

            // Add ObjectHandleUtility's ExposeToCPP to the receiver.
            INamedTypeSymbol? objectHandleUtilityType = compilation.GetTypeByMetadataName("Reinterop.ObjectHandleUtility");
            if (objectHandleUtilityType != null)
            {
                var exposeToCpp = CSharpTypeUtility.FindMembers(objectHandleUtilityType, "ExposeToCPP");
                foreach (ISymbol symbol in exposeToCpp)
                {
                    IMethodSymbol? method = symbol as IMethodSymbol;
                    if (method == null)
                        continue;

                    foreach (var reference in method.DeclaringSyntaxReferences)
                    {
                        if (reference.GetSyntax() is MethodDeclarationSyntax methodDeclaration)
                        {
                            receiver.ExposeToCppMethods.Add(methodDeclaration);
                        }
                    }
                }
            }

            List<IEnumerable<TypeToGenerate>> typesToGenerate = new List<IEnumerable<TypeToGenerate>>();
            CodeGenerator codeGenerator = CreateCodeGenerator(context.AnalyzerConfigOptions, compilation);

            foreach (MethodDeclarationSyntax exposeMethod in receiver.ExposeToCppMethods)
            {
                SemanticModel semanticModel = compilation.GetSemanticModel(exposeMethod.SyntaxTree);
                ExposeToCppSyntaxWalker walker = new ExposeToCppSyntaxWalker(codeGenerator.Options, semanticModel);
                walker.Visit(exposeMethod);
                typesToGenerate.Add(walker.GenerationItems.Values);
            }

            foreach (AttributeSyntax attributeSyntax in receiver.ClassesImplementedInCpp)
            {
                var args = attributeSyntax.ArgumentList!.Arguments;
                if (args.Count < 2)
                    // TODO: report insufficient arguments. Can this even happen?
                    continue;

                var classSyntax = attributeSyntax.Parent?.Parent as ClassDeclarationSyntax;
                if (classSyntax == null)
                    continue;

                var implClassName = (args[0]?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
                var implHeaderName = (args[1]?.Expression as LiteralExpressionSyntax)?.Token.ValueText;

                // A C# class that is meant to be implemented in C++.
                SemanticModel semanticModel = compilation.GetSemanticModel(attributeSyntax.SyntaxTree);
                ITypeSymbol? type = semanticModel.GetDeclaredSymbol(classSyntax) as ITypeSymbol;

                ExposeToCppSyntaxWalker walker = new ExposeToCppSyntaxWalker(codeGenerator.Options, semanticModel);

                if (type != null)
                {
                    TypeToGenerate item;
                    if (!walker.GenerationItems.TryGetValue(type, out item))
                    {
                        item = new TypeToGenerate(type);
                        walker.GenerationItems.Add(type, item);
                    }

                    item.ImplementationClassName = implClassName;
                    item.ImplementationHeaderName = implHeaderName;

                    foreach (MemberDeclarationSyntax memberSyntax in classSyntax.Members)
                    {
                        MethodDeclarationSyntax? methodSyntax = memberSyntax as MethodDeclarationSyntax;
                        if (methodSyntax == null)
                            continue;

                        if (methodSyntax.Modifiers.IndexOf(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword) >= 0)
                        {
                            IMethodSymbol? symbol = semanticModel.GetDeclaredSymbol(methodSyntax) as IMethodSymbol;
                            if (symbol != null)
                            {
                                item.MethodsImplementedInCpp.Add(symbol);
                            }
                        }
                    }
                }

                typesToGenerate.Add(walker.GenerationItems.Values);
            }

            // Create a unique entry for each type
            var typeDictionary = TypeToGenerate.Combine(typesToGenerate);

            // Process the generation items, for example, linking them together.
            foreach (TypeToGenerate item in typeDictionary.Values)
            {
                InheritanceChainer.Chain(item, typeDictionary);
            }

            List<GeneratedResult> generatedResults = new List<GeneratedResult>();
            foreach (TypeToGenerate item in typeDictionary.Values)
            {
                GeneratedResult? result = codeGenerator.GenerateType(item);
                if (result != null)
                    generatedResults.Add(result);
            }

            IEnumerable<CppSourceFile> sourceFiles = codeGenerator.DistributeToSourceFiles(generatedResults);
            foreach (CppSourceFile sourceFile in sourceFiles)
            {
                sourceFile.Write(codeGenerator.Options);
            }

            CodeGenerator.WriteCSharpCode(context, codeGenerator.Options, generatedResults);
        }

        private CodeGenerator CreateCodeGenerator(AnalyzerConfigOptionsProvider options, Compilation compilation)
        {
            CppGenerationContext cppContext = new CppGenerationContext(compilation);

            string? projectDir;
            if (!options.GlobalOptions.TryGetValue("build_property.projectdir", out projectDir))
                projectDir = "";

            string? cppOutputPath;
            if (!options.GlobalOptions.TryGetValue("cpp_output_path", out cppOutputPath))
                cppOutputPath = "generated";

            cppContext.OutputDirectory = Path.GetFullPath(Path.Combine(projectDir, cppOutputPath));

            string? baseNamespace;
            if (!options.GlobalOptions.TryGetValue("base_namespace", out baseNamespace))
                baseNamespace = "DotNet";

            cppContext.BaseNamespace = baseNamespace;

            string? nativeLibraryName;
            if (!options.GlobalOptions.TryGetValue("native_library_name", out nativeLibraryName))
                nativeLibraryName = "ReinteropNative";

            cppContext.NativeLibraryName = nativeLibraryName;

            string? nonBlittableTypes;
            if (!options.GlobalOptions.TryGetValue("non_blittable_types", out nonBlittableTypes))
                nonBlittableTypes = "";

            cppContext.NonBlittableTypes.UnionWith(nonBlittableTypes.Split(',').Select(t => t.Trim()));

            cppContext.CustomGenerators.Add(new CustomStringGenerator());
            cppContext.CustomGenerators.Add(new CustomDelegateGenerator());
            cppContext.CustomGenerators.Add(new CustomArrayGenerator());

            return new CodeGenerator(cppContext);
        }

        private static string? GetAttributeName(AttributeSyntax attribute)
        {
            NameSyntax? name = attribute.Name;
            SimpleNameSyntax? simpleName = name as SimpleNameSyntax;
            if (simpleName != null)
                return simpleName.Identifier.Text;

            QualifiedNameSyntax? qualifiedName = name as QualifiedNameSyntax;
            if (qualifiedName != null)
                return qualifiedName.Right.Identifier.Text;

            return null;
        }

        private class ReinteropSyntaxReceiver : ISyntaxReceiver
        {
            public readonly List<MethodDeclarationSyntax> ExposeToCppMethods = new List<MethodDeclarationSyntax>();
            public readonly List<AttributeSyntax> ClassesImplementedInCpp = new List<AttributeSyntax>();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                var attributeNode = syntaxNode as AttributeSyntax;
                if (attributeNode == null)
                    return;

                string? attributeName = GetAttributeName(attributeNode);
                if (attributeName != "Reinterop" &&
                    attributeName != "ReinteropAttribute" &&
                    attributeName != "ReinteropNativeImplementation" &&
                    attributeName != "ReinteropNativeImplementationAttribute")
                {
                    return;
                }

                var classSyntax = attributeNode.Parent?.Parent as ClassDeclarationSyntax;
                if (classSyntax == null)
                    return;

                if (attributeName == "Reinterop" || attributeName == "ReinteropAttribute")
                {
                    // A C# class containing a method that identifies what types, methods, properties, etc. should be accessible from C++.
                    foreach (MemberDeclarationSyntax memberSyntax in classSyntax.Members)
                    {
                        MethodDeclarationSyntax? methodSyntax = memberSyntax as MethodDeclarationSyntax;
                        if (methodSyntax == null)
                            continue;

                        if (string.Equals(methodSyntax.Identifier.Text, "ExposeToCPP", StringComparison.InvariantCultureIgnoreCase))
                            ExposeToCppMethods.Add(methodSyntax);
                    }
                }
                else if (attributeName == "ReinteropNativeImplementation" || attributeName == "ReinteropNativeImplementationAttribute")
                {
                    // A class with partial methods intended to be implemented in C++.
                    ClassesImplementedInCpp.Add(attributeNode);
                }
            }
        }
    }
}
