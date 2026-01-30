using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CompileTimeLogger
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class CompileTimeLoggerAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "CTL001";

        private static readonly LocalizableString Title = new LocalizableResourceString(
            nameof(Resources.AnalyzerTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(
            nameof(Resources.AnalyzerMessageFormat), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString Description = new LocalizableResourceString(
            nameof(Resources.AnalyzerDescription), Resources.ResourceManager, typeof(Resources));

        private const string Category = "Performance";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        private static readonly string[] LogMethodNames = new[]
        {
            "LogTrace",
            "LogDebug",
            "LogInformation",
            "LogWarning",
            "LogError",
            "LogCritical"
        };

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            // Check if this is a member access expression (e.g., _logger.LogInformation)
            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess))
                return;

            var methodName = memberAccess.Name.Identifier.Text;

            // Check if it's one of the Log* methods we're interested in
            if (!LogMethodNames.Contains(methodName))
                return;

            // Get semantic model to verify this is actually ILogger
            var semanticModel = context.SemanticModel;
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, context.CancellationToken);

            if (!(symbolInfo.Symbol is IMethodSymbol methodSymbol))
                return;

            // Check if the method is from ILogger or ILogger<T>
            var containingType = methodSymbol.ContainingType;
            if (containingType == null)
                return;

            // Check if this is an extension method on ILogger
            var receiverType = GetReceiverType(memberAccess, semanticModel, context.CancellationToken);
            if (receiverType == null)
                return;

            if (!IsILoggerType(receiverType) && !IsILoggerType(containingType))
                return;

            // Check if there's a message template (string argument)
            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count == 0)
                return;

            // Find the message template argument (string literal or interpolated string)
            var hasMessageTemplate = arguments.Any(arg =>
                (arg.Expression is LiteralExpressionSyntax literal &&
                 literal.IsKind(SyntaxKind.StringLiteralExpression)) ||
                arg.Expression is InterpolatedStringExpressionSyntax);

            if (!hasMessageTemplate)
                return;

            // Report diagnostic
            var diagnostic = Diagnostic.Create(
                Rule,
                invocation.GetLocation(),
                methodName);

            context.ReportDiagnostic(diagnostic);
        }

        private static ITypeSymbol GetReceiverType(
            MemberAccessExpressionSyntax memberAccess,
            SemanticModel semanticModel,
            System.Threading.CancellationToken cancellationToken)
        {
            var typeInfo = semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken);
            return typeInfo.Type;
        }

        private static bool IsILoggerType(ITypeSymbol type)
        {
            if (type == null)
                return false;

            // Check for ILogger or ILogger<T>
            if (type.Name == "ILogger" &&
                type.ContainingNamespace?.ToDisplayString() == "Microsoft.Extensions.Logging")
                return true;

            // Check interfaces
            foreach (var iface in type.AllInterfaces)
            {
                if (iface.Name == "ILogger" &&
                    iface.ContainingNamespace?.ToDisplayString() == "Microsoft.Extensions.Logging")
                    return true;
            }

            return false;
        }
    }
}
