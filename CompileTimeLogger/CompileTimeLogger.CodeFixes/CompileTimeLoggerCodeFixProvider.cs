using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace CompileTimeLogger
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CompileTimeLoggerCodeFixProvider)), Shared]
    public class CompileTimeLoggerCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(CompileTimeLoggerAnalyzer.DiagnosticId);

        public sealed override FixAllProvider GetFixAllProvider() =>
            WellKnownFixAllProviders.BatchFixer;

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            var invocation = root.FindToken(diagnosticSpan.Start)
                .Parent
                .AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .First();

            // Register code fix for instance method
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: CodeFixResources.CodeFixTitleInstanceMethod,
                    createChangedDocument: c => ConvertToInstanceMethodAsync(context.Document, invocation, c),
                    equivalenceKey: nameof(CodeFixResources.CodeFixTitleInstanceMethod)),
                diagnostic);

            // Register code fix for Log class
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: CodeFixResources.CodeFixTitleLogClass,
                    createChangedDocument: c => ConvertToLogClassAsync(context.Document, invocation, c),
                    equivalenceKey: nameof(CodeFixResources.CodeFixTitleLogClass)),
                diagnostic);
        }

        private async Task<Document> ConvertToInstanceMethodAsync(
            Document document,
            InvocationExpressionSyntax invocation,
            CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var logCallInfo = ExtractLogCallInfo(invocation, semanticModel);
            if (logCallInfo == null)
                return document;

            var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (containingClass == null)
                return document;

            // Find the logger field name
            var loggerFieldName = GetLoggerFieldName(invocation);

            // Generate method name from message template
            var methodName = GenerateMethodName(logCallInfo.MessageTemplate);

            // Generate the new partial method
            var partialMethod = GenerateInstancePartialMethod(
                methodName,
                logCallInfo.LogLevel,
                logCallInfo.MessageTemplate,
                logCallInfo.Parameters,
                logCallInfo.HasException);

            // Generate the new invocation
            var newInvocation = GenerateInstanceMethodInvocation(
                methodName,
                logCallInfo.Parameters,
                logCallInfo.ExceptionArgument);

            // Replace the invocation
            var newRoot = root.ReplaceNode(invocation, newInvocation);

            // Find the class again in the new root
            var newClass = newRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == containingClass.Identifier.Text);

            if (newClass == null)
                return document;

            // Make the class partial if not already
            var updatedClass = EnsureClassIsPartial(newClass);

            // Add the partial method to the class
            updatedClass = AddMemberToClass(updatedClass, partialMethod);

            newRoot = newRoot.ReplaceNode(newClass, updatedClass);

            // Add using directive if needed
            newRoot = AddUsingDirectiveIfNeeded(newRoot, "Microsoft.Extensions.Logging");

            return document.WithSyntaxRoot(newRoot);
        }

        private async Task<Document> ConvertToLogClassAsync(
            Document document,
            InvocationExpressionSyntax invocation,
            CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var logCallInfo = ExtractLogCallInfo(invocation, semanticModel);
            if (logCallInfo == null)
                return document;

            var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (containingClass == null)
                return document;

            // Find the logger field name
            var loggerFieldName = GetLoggerFieldName(invocation);

            // Generate method name from message template (without "Log" prefix for nested class)
            var methodName = GenerateMethodNameWithoutPrefix(logCallInfo.MessageTemplate);

            // Generate the static method for the Log class
            var staticMethod = GenerateStaticLogMethod(
                methodName,
                logCallInfo.LogLevel,
                logCallInfo.MessageTemplate,
                logCallInfo.Parameters,
                logCallInfo.HasException);

            // Generate the new invocation
            var newInvocation = GenerateLogClassInvocation(
                methodName,
                loggerFieldName,
                logCallInfo.Parameters,
                logCallInfo.ExceptionArgument);

            // Replace the invocation
            var newRoot = root.ReplaceNode(invocation, newInvocation);

            // Find the class again in the new root
            var newClass = newRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == containingClass.Identifier.Text);

            if (newClass == null)
                return document;

            // Make the class partial if not already
            var updatedClass = EnsureClassIsPartial(newClass);

            // Find or create the nested Log class
            var logClass = updatedClass.Members
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == "Log");

            if (logClass != null)
            {
                // Add method to existing Log class
                var updatedLogClass = AddMemberToClass(logClass, staticMethod);
                updatedClass = updatedClass.ReplaceNode(logClass, updatedLogClass);
            }
            else
            {
                // Create new Log class
                logClass = CreateNestedLogClass(staticMethod);
                updatedClass = AddMemberToClass(updatedClass, logClass);
            }

            newRoot = newRoot.ReplaceNode(newClass, updatedClass);

            // Add using directive if needed
            newRoot = AddUsingDirectiveIfNeeded(newRoot, "Microsoft.Extensions.Logging");

            return document.WithSyntaxRoot(newRoot);
        }

        private class LogCallInfo
        {
            public string LogLevel { get; set; }
            public string MessageTemplate { get; set; }
            public List<ParameterInfo> Parameters { get; set; } = new List<ParameterInfo>();
            public bool HasException { get; set; }
            public ExpressionSyntax ExceptionArgument { get; set; }
        }

        private class ParameterInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public ExpressionSyntax Argument { get; set; }
        }

        private LogCallInfo ExtractLogCallInfo(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess))
                return null;

            var methodName = memberAccess.Name.Identifier.Text;
            var logLevel = GetLogLevelFromMethodName(methodName);

            var arguments = invocation.ArgumentList.Arguments.ToList();
            if (arguments.Count == 0)
                return null;

            var info = new LogCallInfo { LogLevel = logLevel };

            int messageIndex = 0;

            // Check if first argument is an exception
            if (arguments.Count > 0)
            {
                var firstArgType = semanticModel.GetTypeInfo(arguments[0].Expression).Type;
                if (firstArgType != null && IsExceptionType(firstArgType))
                {
                    info.HasException = true;
                    info.ExceptionArgument = arguments[0].Expression;
                    messageIndex = 1;
                }
            }

            // Find the message template
            if (messageIndex >= arguments.Count)
                return null;

            var messageArg = arguments[messageIndex];
            if (messageArg.Expression is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                info.MessageTemplate = literal.Token.ValueText;
            }
            else
            {
                return null;
            }

            // Extract placeholders from message template
            var placeholders = ExtractPlaceholders(info.MessageTemplate);

            // Match placeholders with remaining arguments
            var paramArguments = arguments.Skip(messageIndex + 1).ToList();
            for (int i = 0; i < placeholders.Count && i < paramArguments.Count; i++)
            {
                var argType = semanticModel.GetTypeInfo(paramArguments[i].Expression).Type;
                info.Parameters.Add(new ParameterInfo
                {
                    Name = ToCamelCase(placeholders[i]),
                    Type = argType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "object",
                    Argument = paramArguments[i].Expression
                });
            }

            return info;
        }

        private static List<string> ExtractPlaceholders(string messageTemplate)
        {
            var placeholders = new List<string>();
            var regex = new Regex(@"\{([^}:]+)(?::[^}]*)?\}");
            foreach (Match match in regex.Matches(messageTemplate))
            {
                placeholders.Add(match.Groups[1].Value);
            }
            return placeholders;
        }

        private static string GetLogLevelFromMethodName(string methodName)
        {
            switch (methodName)
            {
                case "LogTrace": return "Trace";
                case "LogDebug": return "Debug";
                case "LogInformation": return "Information";
                case "LogWarning": return "Warning";
                case "LogError": return "Error";
                case "LogCritical": return "Critical";
                default: return "Information";
            }
        }

        private static bool IsExceptionType(ITypeSymbol type)
        {
            var current = type;
            while (current != null)
            {
                if (current.Name == "Exception" &&
                    current.ContainingNamespace?.ToDisplayString() == "System")
                    return true;
                current = current.BaseType;
            }
            return false;
        }

        private static string GetLoggerFieldName(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                return memberAccess.Expression.ToString();
            }
            return "_logger";
        }

        private static string GenerateMethodName(string messageTemplate)
        {
            return "Log" + GenerateMethodNameWithoutPrefix(messageTemplate);
        }

        private static string GenerateMethodNameWithoutPrefix(string messageTemplate)
        {
            // Remove placeholders
            var withoutPlaceholders = Regex.Replace(messageTemplate, @"\{[^}]+\}", "");

            // Split into words and convert to PascalCase
            var words = Regex.Split(withoutPlaceholders, @"[^a-zA-Z0-9]+")
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant());

            var name = string.Join("", words);

            if (string.IsNullOrEmpty(name))
                name = "Message";

            return name;
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static MethodDeclarationSyntax GenerateInstancePartialMethod(
            string methodName,
            string logLevel,
            string messageTemplate,
            List<ParameterInfo> parameters,
            bool hasException)
        {
            var parameterList = new List<ParameterSyntax>();

            if (hasException)
            {
                parameterList.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier("exception"))
                    .WithType(SyntaxFactory.ParseTypeName("Exception")));
            }

            foreach (var param in parameters)
            {
                parameterList.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(param.Name))
                    .WithType(SyntaxFactory.ParseTypeName(param.Type)));
            }

            var attribute = SyntaxFactory.Attribute(
                SyntaxFactory.IdentifierName("LoggerMessage"))
                .WithArgumentList(SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SeparatedList(new[]
                    {
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("LogLevel"),
                                SyntaxFactory.IdentifierName(logLevel)))
                            .WithNameEquals(SyntaxFactory.NameEquals("Level")),
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(messageTemplate)))
                            .WithNameEquals(SyntaxFactory.NameEquals("Message"))
                    })));

            return SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    SyntaxFactory.Identifier(methodName))
                .WithAttributeLists(SyntaxFactory.SingletonList(
                    SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute))))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                .WithParameterList(SyntaxFactory.ParameterList(
                    SyntaxFactory.SeparatedList(parameterList)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static MethodDeclarationSyntax GenerateStaticLogMethod(
            string methodName,
            string logLevel,
            string messageTemplate,
            List<ParameterInfo> parameters,
            bool hasException)
        {
            var parameterList = new List<ParameterSyntax>
            {
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("logger"))
                    .WithType(SyntaxFactory.ParseTypeName("ILogger"))
            };

            if (hasException)
            {
                parameterList.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier("exception"))
                    .WithType(SyntaxFactory.ParseTypeName("Exception")));
            }

            foreach (var param in parameters)
            {
                parameterList.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(param.Name))
                    .WithType(SyntaxFactory.ParseTypeName(param.Type)));
            }

            var attribute = SyntaxFactory.Attribute(
                SyntaxFactory.IdentifierName("LoggerMessage"))
                .WithArgumentList(SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SeparatedList(new[]
                    {
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("LogLevel"),
                                SyntaxFactory.IdentifierName(logLevel)))
                            .WithNameEquals(SyntaxFactory.NameEquals("Level")),
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(messageTemplate)))
                            .WithNameEquals(SyntaxFactory.NameEquals("Message"))
                    })));

            return SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    SyntaxFactory.Identifier(methodName))
                .WithAttributeLists(SyntaxFactory.SingletonList(
                    SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute))))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                    SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                .WithParameterList(SyntaxFactory.ParameterList(
                    SyntaxFactory.SeparatedList(parameterList)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static InvocationExpressionSyntax GenerateInstanceMethodInvocation(
            string methodName,
            List<ParameterInfo> parameters,
            ExpressionSyntax exceptionArgument)
        {
            var arguments = new List<ArgumentSyntax>();

            if (exceptionArgument != null)
            {
                arguments.Add(SyntaxFactory.Argument(exceptionArgument));
            }

            foreach (var param in parameters)
            {
                arguments.Add(SyntaxFactory.Argument(param.Argument));
            }

            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.IdentifierName(methodName))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(arguments)));
        }

        private static InvocationExpressionSyntax GenerateLogClassInvocation(
            string methodName,
            string loggerFieldName,
            List<ParameterInfo> parameters,
            ExpressionSyntax exceptionArgument)
        {
            var arguments = new List<ArgumentSyntax>
            {
                SyntaxFactory.Argument(SyntaxFactory.ParseExpression(loggerFieldName))
            };

            if (exceptionArgument != null)
            {
                arguments.Add(SyntaxFactory.Argument(exceptionArgument));
            }

            foreach (var param in parameters)
            {
                arguments.Add(SyntaxFactory.Argument(param.Argument));
            }

            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("Log"),
                    SyntaxFactory.IdentifierName(methodName)))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(arguments)));
        }

        private static ClassDeclarationSyntax EnsureClassIsPartial(ClassDeclarationSyntax classDeclaration)
        {
            if (classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                return classDeclaration;

            var newModifiers = classDeclaration.Modifiers.Add(
                SyntaxFactory.Token(SyntaxKind.PartialKeyword));

            return classDeclaration.WithModifiers(newModifiers);
        }

        private static ClassDeclarationSyntax AddMemberToClass(
            ClassDeclarationSyntax classDeclaration,
            MemberDeclarationSyntax member)
        {
            return classDeclaration.AddMembers(member);
        }

        private static ClassDeclarationSyntax CreateNestedLogClass(MethodDeclarationSyntax method)
        {
            return SyntaxFactory.ClassDeclaration("Log")
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                    SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(method))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static SyntaxNode AddUsingDirectiveIfNeeded(SyntaxNode root, string namespaceName)
        {
            if (root is CompilationUnitSyntax compilationUnit)
            {
                var hasUsing = compilationUnit.Usings.Any(u =>
                    u.Name.ToString() == namespaceName);

                if (!hasUsing)
                {
                    var usingDirective = SyntaxFactory.UsingDirective(
                        SyntaxFactory.ParseName(namespaceName))
                        .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                    return compilationUnit.AddUsings(usingDirective);
                }
            }

            return root;
        }
    }
}
