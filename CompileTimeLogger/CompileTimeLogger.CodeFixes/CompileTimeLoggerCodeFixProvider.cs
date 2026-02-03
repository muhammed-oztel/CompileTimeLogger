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
using Microsoft.CodeAnalysis.Formatting;

namespace CompileTimeLogger
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CompileTimeLoggerCodeFixProvider)), Shared]
    public class CompileTimeLoggerCodeFixProvider : CodeFixProvider
    {
        private static readonly SyntaxAnnotation InvocationAnnotation = new SyntaxAnnotation("LogInvocation");

        public sealed override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(CompileTimeLoggerAnalyzer.DiagnosticId);

        public sealed override FixAllProvider GetFixAllProvider() =>
            new CompileTimeLoggerFixAllProvider();

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            // Register code fix for instance method
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: CodeFixResources.CodeFixTitleInstanceMethod,
                    createChangedDocument: c => ConvertToInstanceMethodAsync(context.Document, diagnosticSpan, c),
                    equivalenceKey: nameof(CodeFixResources.CodeFixTitleInstanceMethod)),
                diagnostic);

            // Register code fix for Log class
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: CodeFixResources.CodeFixTitleLogClass,
                    createChangedDocument: c => ConvertToLogClassAsync(context.Document, diagnosticSpan, c),
                    equivalenceKey: nameof(CodeFixResources.CodeFixTitleLogClass)),
                diagnostic);
        }

        private async Task<Document> ConvertToInstanceMethodAsync(
            Document document,
            Microsoft.CodeAnalysis.Text.TextSpan diagnosticSpan,
            CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var invocation = root.FindToken(diagnosticSpan.Start)
                .Parent
                .AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault();

            if (invocation == null)
                return document;

            return await ConvertToInstanceMethodCoreAsync(document, semanticModel, root, invocation, cancellationToken);
        }

        internal static async Task<Document> ConvertToInstanceMethodCoreAsync(
            Document document,
            SemanticModel semanticModel,
            SyntaxNode root,
            InvocationExpressionSyntax invocation,
            CancellationToken cancellationToken)
        {
            var logCallInfo = ExtractLogCallInfo(invocation, semanticModel);
            if (logCallInfo == null)
                return document;

            var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (containingClass == null)
                return document;

            var methodName = GenerateMethodName(logCallInfo.MessageTemplate);

            // If the partial method already exists (duplicate message), only replace the call site
            var existingMethod = containingClass.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);

            var newInvocation = GenerateInstanceMethodInvocation(
                methodName,
                logCallInfo.Parameters,
                logCallInfo.ExceptionArgument);

            var newRoot = root.ReplaceNode(invocation, newInvocation);

            if (existingMethod != null)
                return document.WithSyntaxRoot(newRoot);

            // Locate the containing class in the updated tree
            var newClass = newRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == containingClass.Identifier.Text);

            if (newClass == null)
                return document;

            var updatedClass = EnsureClassIsPartial(newClass);

            updatedClass = AddMemberToClass(updatedClass, GenerateInstancePartialMethod(
                methodName,
                logCallInfo.LogLevel,
                logCallInfo.MessageTemplate,
                logCallInfo.Parameters,
                logCallInfo.HasException));

            newRoot = newRoot.ReplaceNode(newClass, updatedClass);

            newRoot = AddUsingDirectiveIfNeeded(newRoot, "Microsoft.Extensions.Logging");

            return document.WithSyntaxRoot(newRoot);
        }

        private async Task<Document> ConvertToLogClassAsync(
            Document document,
            Microsoft.CodeAnalysis.Text.TextSpan diagnosticSpan,
            CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var invocation = root.FindToken(diagnosticSpan.Start)
                .Parent
                .AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault();

            if (invocation == null)
                return document;

            return await ConvertToLogClassCoreAsync(document, semanticModel, root, invocation, cancellationToken);
        }

        internal static async Task<Document> ConvertToLogClassCoreAsync(
            Document document,
            SemanticModel semanticModel,
            SyntaxNode root,
            InvocationExpressionSyntax invocation,
            CancellationToken cancellationToken)
        {
            var logCallInfo = ExtractLogCallInfo(invocation, semanticModel);
            if (logCallInfo == null)
                return document;

            var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (containingClass == null)
                return document;

            var loggerFieldName = GetLoggerFieldName(invocation);
            var methodName = GenerateMethodNameWithoutPrefix(logCallInfo.MessageTemplate);

            // If the Log class already has this exact method (duplicate message), only replace the call site
            var existingLogClass = containingClass.Members
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == "Log");

            bool methodAlreadyExists = existingLogClass != null &&
                existingLogClass.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Any(m => m.Identifier.Text == methodName);

            var newInvocation = GenerateLogClassInvocation(
                methodName,
                loggerFieldName,
                logCallInfo.Parameters,
                logCallInfo.ExceptionArgument);

            var newRoot = root.ReplaceNode(invocation, newInvocation);

            if (methodAlreadyExists)
                return document.WithSyntaxRoot(newRoot);

            // Locate the containing class in the updated tree
            var newClass = newRoot.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == containingClass.Identifier.Text);

            if (newClass == null)
                return document;

            var updatedClass = EnsureClassIsPartial(newClass);

            var staticMethod = GenerateStaticLogMethod(
                methodName,
                logCallInfo.LogLevel,
                logCallInfo.MessageTemplate,
                logCallInfo.Parameters,
                logCallInfo.HasException);

            // Add the method to the existing Log class, or create one
            var logClass = updatedClass.Members
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == "Log");

            if (logClass != null)
            {
                updatedClass = updatedClass.ReplaceNode(logClass, AddMemberToClass(logClass, staticMethod));
            }
            else
            {
                updatedClass = AddMemberToClass(updatedClass, CreateNestedLogClass(staticMethod));
            }

            newRoot = newRoot.ReplaceNode(newClass, updatedClass);

            newRoot = AddUsingDirectiveIfNeeded(newRoot, "Microsoft.Extensions.Logging");

            return document.WithSyntaxRoot(newRoot);
        }

        internal class LogCallInfo
        {
            public string LogLevel { get; set; }
            public string MessageTemplate { get; set; }
            public List<ParameterInfo> Parameters { get; set; } = new List<ParameterInfo>();
            public bool HasException { get; set; }
            public ExpressionSyntax ExceptionArgument { get; set; }
        }

        internal class ParameterInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public ExpressionSyntax Argument { get; set; }
        }

        internal static LogCallInfo ExtractLogCallInfo(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
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

            // Handle regular string literal
            if (messageArg.Expression is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                info.MessageTemplate = literal.Token.ValueText;

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
            }
            // Handle interpolated string
            else if (messageArg.Expression is InterpolatedStringExpressionSyntax interpolatedString)
            {
                var (messageTemplate, parameters) = ExtractFromInterpolatedString(interpolatedString, semanticModel);
                info.MessageTemplate = messageTemplate;
                info.Parameters = parameters;
            }
            else
            {
                return null;
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

        private static (string MessageTemplate, List<ParameterInfo> Parameters) ExtractFromInterpolatedString(
            InterpolatedStringExpressionSyntax interpolatedString,
            SemanticModel semanticModel)
        {
            var messageTemplate = new StringBuilder();
            var parameters = new List<ParameterInfo>();
            var placeholderIndex = 0;

            foreach (var content in interpolatedString.Contents)
            {
                if (content is InterpolatedStringTextSyntax textSyntax)
                {
                    // Add the literal text
                    messageTemplate.Append(textSyntax.TextToken.ValueText);
                }
                else if (content is InterpolationSyntax interpolation)
                {
                    // Extract the expression
                    var expression = interpolation.Expression;
                    var expressionText = expression.ToString();

                    // Generate placeholder name from expression
                    var placeholderName = GeneratePlaceholderName(expressionText, placeholderIndex);
                    placeholderIndex++;

                    // Add placeholder to message template
                    if (interpolation.FormatClause != null)
                    {
                        // Preserve format string (e.g., {Value:N2})
                        messageTemplate.Append($"{{{placeholderName}{interpolation.FormatClause}}}");
                    }
                    else
                    {
                        messageTemplate.Append($"{{{placeholderName}}}");
                    }

                    // Get the type of the expression
                    var typeInfo = semanticModel.GetTypeInfo(expression);
                    var type = typeInfo.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "object";

                    // Add parameter info
                    parameters.Add(new ParameterInfo
                    {
                        Name = ToCamelCase(placeholderName),
                        Type = type,
                        Argument = expression
                    });
                }
            }

            return (messageTemplate.ToString(), parameters);
        }

        private static string GeneratePlaceholderName(string expression, int index)
        {
            // Clean up the expression to create a valid parameter name
            // Remove common prefixes and clean up the name
            var cleaned = expression.Trim();

            // Handle member access (e.g., user.Id -> UserId)
            if (cleaned.Contains("."))
            {
                var parts = cleaned.Split('.');
                var name = string.Join("", parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
                return name;
            }

            // Handle array/indexer access (e.g., items[0] -> Items0)
            cleaned = Regex.Replace(cleaned, @"\[(\d+)\]", "$1");
            cleaned = Regex.Replace(cleaned, @"[^\w]", "");

            // Ensure it starts with uppercase
            if (!string.IsNullOrEmpty(cleaned))
            {
                cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned.Substring(1);
            }
            else
            {
                cleaned = $"Value{index}";
            }

            return cleaned;
        }

        internal static string GetLogLevelFromMethodName(string methodName)
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

        internal static string GetLoggerFieldName(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                return memberAccess.Expression.ToString();
            }
            return "_logger";
        }

        internal static string GenerateMethodName(string messageTemplate)
        {
            return "Log" + GenerateMethodNameWithoutPrefix(messageTemplate);
        }

        internal static string GenerateMethodNameWithoutPrefix(string messageTemplate)
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

        internal static MethodDeclarationSyntax GenerateInstancePartialMethod(
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

        internal static MethodDeclarationSyntax GenerateStaticLogMethod(
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

        internal static InvocationExpressionSyntax GenerateInstanceMethodInvocation(
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

        internal static InvocationExpressionSyntax GenerateLogClassInvocation(
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

        internal static ClassDeclarationSyntax EnsureClassIsPartial(ClassDeclarationSyntax classDeclaration)
        {
            if (classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                return classDeclaration;

            var newModifiers = classDeclaration.Modifiers.Add(
                SyntaxFactory.Token(SyntaxKind.PartialKeyword));

            return classDeclaration.WithModifiers(newModifiers);
        }

        internal static ClassDeclarationSyntax AddMemberToClass(
            ClassDeclarationSyntax classDeclaration,
            MemberDeclarationSyntax member)
        {
            return classDeclaration.AddMembers(member);
        }

        internal static ClassDeclarationSyntax CreateNestedLogClass(MethodDeclarationSyntax method)
        {
            return SyntaxFactory.ClassDeclaration("Log")
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                    SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(method))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        internal static SyntaxNode AddUsingDirectiveIfNeeded(SyntaxNode root, string namespaceName)
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

    /// <summary>
    /// Custom FixAllProvider that handles multiple diagnostics in a single document transformation.
    /// </summary>
    internal class CompileTimeLoggerFixAllProvider : FixAllProvider
    {
        public override async Task<CodeAction> GetFixAsync(FixAllContext fixAllContext)
        {
            switch (fixAllContext.Scope)
            {
                case FixAllScope.Document:
                    return CodeAction.Create(
                        GetFixTitle(fixAllContext),
                        c => FixDocumentAsync(fixAllContext, fixAllContext.Document, c),
                        nameof(CompileTimeLoggerFixAllProvider));

                case FixAllScope.Project:
                    return CodeAction.Create(
                        GetFixTitle(fixAllContext),
                        c => FixProjectAsync(fixAllContext, c),
                        nameof(CompileTimeLoggerFixAllProvider));

                case FixAllScope.Solution:
                    return CodeAction.Create(
                        GetFixTitle(fixAllContext),
                        c => FixSolutionAsync(fixAllContext, c),
                        nameof(CompileTimeLoggerFixAllProvider));

                default:
                    return null;
            }
        }

        private static string GetFixTitle(FixAllContext context)
        {
            if (context.CodeActionEquivalenceKey == nameof(CodeFixResources.CodeFixTitleInstanceMethod))
                return CodeFixResources.CodeFixTitleInstanceMethod;
            return CodeFixResources.CodeFixTitleLogClass;
        }

        private async Task<Document> FixDocumentAsync(
            FixAllContext context,
            Document document,
            CancellationToken cancellationToken)
        {
            var diagnostics = await context.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false);
            if (!diagnostics.Any())
                return document;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            // Find all invocations to fix and annotate them
            var invocationsToFix = new List<InvocationExpressionSyntax>();
            foreach (var diagnostic in diagnostics)
            {
                var invocation = root.FindToken(diagnostic.Location.SourceSpan.Start)
                    .Parent
                    .AncestorsAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault();

                if (invocation != null)
                {
                    invocationsToFix.Add(invocation);
                }
            }

            if (!invocationsToFix.Any())
                return document;

            // Annotate all invocations to track them through transformations
            var annotation = new SyntaxAnnotation("LogInvocationToFix");
            var annotatedRoot = root.ReplaceNodes(
                invocationsToFix,
                (original, rewritten) => rewritten.WithAdditionalAnnotations(annotation));

            document = document.WithSyntaxRoot(annotatedRoot);

            // Re-get the root and semantic model after annotation
            root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            // Note: We need to be careful here - the semantic model from before may be stale
            // For fix-all, we'll extract info before transformation using the original semantic model

            // Extract log call info for all annotated invocations using the ORIGINAL semantic model
            // Since the structure hasn't changed yet (just annotations added), this should work
            semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var annotatedInvocations = root.GetAnnotatedNodes(annotation)
                .OfType<InvocationExpressionSyntax>()
                .ToList();

            var logCallInfos = new List<(InvocationExpressionSyntax Invocation, CompileTimeLoggerCodeFixProvider.LogCallInfo Info, string LoggerFieldName)>();

            foreach (var invocation in annotatedInvocations)
            {
                var info = CompileTimeLoggerCodeFixProvider.ExtractLogCallInfo(invocation, semanticModel);
                if (info != null)
                {
                    var loggerFieldName = CompileTimeLoggerCodeFixProvider.GetLoggerFieldName(invocation);
                    logCallInfos.Add((invocation, info, loggerFieldName));
                }
            }

            if (!logCallInfos.Any())
                return document;

            // Now apply the fix based on the equivalence key
            if (context.CodeActionEquivalenceKey == nameof(CodeFixResources.CodeFixTitleInstanceMethod))
            {
                return await ApplyInstanceMethodFixAsync(document, logCallInfos, cancellationToken);
            }
            else
            {
                return await ApplyLogClassFixAsync(document, logCallInfos, cancellationToken);
            }
        }

        private async Task<Document> ApplyInstanceMethodFixAsync(
            Document document,
            List<(InvocationExpressionSyntax Invocation, CompileTimeLoggerCodeFixProvider.LogCallInfo Info, string LoggerFieldName)> logCallInfos,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            // Group by containing class
            var byClass = logCallInfos
                .GroupBy(x => x.Invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text)
                .Where(g => g.Key != null);

            // First, replace all invocations with their new calls
            var replacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();
            var methodsToAdd = new Dictionary<string, List<MethodDeclarationSyntax>>(); // className -> methods

            foreach (var classGroup in byClass)
            {
                var className = classGroup.Key;
                var addedMethodNames = new HashSet<string>();
                var methods = new List<MethodDeclarationSyntax>();

                foreach (var (invocation, info, _) in classGroup)
                {
                    var methodName = CompileTimeLoggerCodeFixProvider.GenerateMethodName(info.MessageTemplate);

                    var newInvocation = CompileTimeLoggerCodeFixProvider.GenerateInstanceMethodInvocation(
                        methodName,
                        info.Parameters,
                        info.ExceptionArgument);

                    replacements[invocation] = newInvocation;

                    // Only add method if not already added (handles duplicates)
                    if (!addedMethodNames.Contains(methodName))
                    {
                        addedMethodNames.Add(methodName);
                        methods.Add(CompileTimeLoggerCodeFixProvider.GenerateInstancePartialMethod(
                            methodName,
                            info.LogLevel,
                            info.MessageTemplate,
                            info.Parameters,
                            info.HasException));
                    }
                }

                methodsToAdd[className] = methods;
            }

            // Replace all invocations
            var newRoot = root.ReplaceNodes(
                replacements.Keys,
                (original, rewritten) => replacements[original]);

            // Add methods to each class
            foreach (var kvp in methodsToAdd)
            {
                var className = kvp.Key;
                var methods = kvp.Value;
                var classDecl = newRoot.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(c => c.Identifier.Text == className);

                if (classDecl != null)
                {
                    // Check for existing methods with same names
                    var existingMethodNames = new HashSet<string>(classDecl.Members
                        .OfType<MethodDeclarationSyntax>()
                        .Select(m => m.Identifier.Text));

                    var methodsToActuallyAdd = methods
                        .Where(m => !existingMethodNames.Contains(m.Identifier.Text))
                        .ToArray();

                    if (methodsToActuallyAdd.Any())
                    {
                        var updatedClass = CompileTimeLoggerCodeFixProvider.EnsureClassIsPartial(classDecl);
                        updatedClass = updatedClass.AddMembers(methodsToActuallyAdd);
                        newRoot = newRoot.ReplaceNode(classDecl, updatedClass);
                    }
                    else
                    {
                        // Still ensure partial
                        var updatedClass = CompileTimeLoggerCodeFixProvider.EnsureClassIsPartial(classDecl);
                        if (updatedClass != classDecl)
                        {
                            newRoot = newRoot.ReplaceNode(classDecl, updatedClass);
                        }
                    }
                }
            }

            newRoot = CompileTimeLoggerCodeFixProvider.AddUsingDirectiveIfNeeded(newRoot, "Microsoft.Extensions.Logging");

            return document.WithSyntaxRoot(newRoot);
        }

        private async Task<Document> ApplyLogClassFixAsync(
            Document document,
            List<(InvocationExpressionSyntax Invocation, CompileTimeLoggerCodeFixProvider.LogCallInfo Info, string LoggerFieldName)> logCallInfos,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            // Group by containing class
            var byClass = logCallInfos
                .GroupBy(x => x.Invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text)
                .Where(g => g.Key != null);

            // First, replace all invocations with their new calls
            var replacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();
            var methodsToAdd = new Dictionary<string, List<MethodDeclarationSyntax>>(); // className -> methods

            foreach (var classGroup in byClass)
            {
                var className = classGroup.Key;
                var addedMethodNames = new HashSet<string>();
                var methods = new List<MethodDeclarationSyntax>();

                foreach (var (invocation, info, loggerFieldName) in classGroup)
                {
                    var methodName = CompileTimeLoggerCodeFixProvider.GenerateMethodNameWithoutPrefix(info.MessageTemplate);

                    var newInvocation = CompileTimeLoggerCodeFixProvider.GenerateLogClassInvocation(
                        methodName,
                        loggerFieldName,
                        info.Parameters,
                        info.ExceptionArgument);

                    replacements[invocation] = newInvocation;

                    // Only add method if not already added (handles duplicates)
                    if (!addedMethodNames.Contains(methodName))
                    {
                        addedMethodNames.Add(methodName);
                        methods.Add(CompileTimeLoggerCodeFixProvider.GenerateStaticLogMethod(
                            methodName,
                            info.LogLevel,
                            info.MessageTemplate,
                            info.Parameters,
                            info.HasException));
                    }
                }

                methodsToAdd[className] = methods;
            }

            // Replace all invocations
            var newRoot = root.ReplaceNodes(
                replacements.Keys,
                (original, rewritten) => replacements[original]);

            // Add Log class with methods to each containing class
            foreach (var kvp in methodsToAdd)
            {
                var className = kvp.Key;
                var methods = kvp.Value;
                var classDecl = newRoot.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(c => c.Identifier.Text == className);

                if (classDecl != null)
                {
                    var updatedClass = CompileTimeLoggerCodeFixProvider.EnsureClassIsPartial(classDecl);

                    // Check for existing Log class
                    var existingLogClass = updatedClass.Members
                        .OfType<ClassDeclarationSyntax>()
                        .FirstOrDefault(c => c.Identifier.Text == "Log");

                    if (existingLogClass != null)
                    {
                        // Add methods to existing Log class (skip duplicates)
                        var existingMethodNames = new HashSet<string>(existingLogClass.Members
                            .OfType<MethodDeclarationSyntax>()
                            .Select(m => m.Identifier.Text));

                        var methodsToActuallyAdd = methods
                            .Where(m => !existingMethodNames.Contains(m.Identifier.Text))
                            .ToArray();

                        if (methodsToActuallyAdd.Any())
                        {
                            var updatedLogClass = existingLogClass.AddMembers(methodsToActuallyAdd);
                            updatedClass = updatedClass.ReplaceNode(existingLogClass, updatedLogClass);
                        }
                    }
                    else
                    {
                        // Create new Log class with all methods
                        var logClass = SyntaxFactory.ClassDeclaration("Log")
                            .WithModifiers(SyntaxFactory.TokenList(
                                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                                SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(methods))
                            .WithAdditionalAnnotations(Formatter.Annotation);

                        updatedClass = updatedClass.AddMembers(logClass);
                    }

                    newRoot = newRoot.ReplaceNode(classDecl, updatedClass);
                }
            }

            newRoot = CompileTimeLoggerCodeFixProvider.AddUsingDirectiveIfNeeded(newRoot, "Microsoft.Extensions.Logging");

            return document.WithSyntaxRoot(newRoot);
        }

        private async Task<Solution> FixProjectAsync(
            FixAllContext context,
            CancellationToken cancellationToken)
        {
            var project = context.Project;
            var solution = project.Solution;

            foreach (var document in project.Documents)
            {
                var fixedDocument = await FixDocumentAsync(context, document, cancellationToken).ConfigureAwait(false);
                solution = solution.WithDocumentSyntaxRoot(
                    document.Id,
                    await fixedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false));
            }

            return solution;
        }

        private async Task<Solution> FixSolutionAsync(
            FixAllContext context,
            CancellationToken cancellationToken)
        {
            var solution = context.Solution;

            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var fixedDocument = await FixDocumentAsync(context, document, cancellationToken).ConfigureAwait(false);
                    solution = solution.WithDocumentSyntaxRoot(
                        document.Id,
                        await fixedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false));
                }
            }

            return solution;
        }
    }
}
