using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VerifyCS = CompileTimeLogger.Test.CSharpCodeFixVerifier<
    CompileTimeLogger.CompileTimeLoggerAnalyzer,
    CompileTimeLogger.CompileTimeLoggerCodeFixProvider>;

namespace CompileTimeLogger.Test
{
    [TestClass]
    public class CompileTimeLoggerUnitTest
    {
        [TestMethod]
        public async Task NoDiagnostic_EmptyCode()
        {
            var test = @"";
            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task NoDiagnostic_NoLoggerCalls()
        {
            var test = @"
using System;

namespace TestNamespace
{
    class TestClass
    {
        public void TestMethod()
        {
            Console.WriteLine(""Hello"");
        }
    }
}";
            await VerifyCS.VerifyAnalyzerAsync(test);
        }

        [TestMethod]
        public async Task Diagnostic_LogInformation_BasicCall()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod()
        {
            {|#0:_logger.LogInformation(""User logged in"")|};
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogInformation");

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Diagnostic_LogWarning_WithParameters()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId)
        {
            {|#0:_logger.LogWarning(""User {UserId} failed login"", userId)|};
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogWarning");

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Diagnostic_LogError_WithException()
        {
            var test = @"
using System;
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod()
        {
            try
            {
                throw new Exception();
            }
            catch (Exception ex)
            {
                {|#0:_logger.LogError(ex, ""An error occurred"")|};
            }
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogError");

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task CodeFix_InstanceMethod_BasicMessage()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod()
        {
            {|#0:_logger.LogInformation(""User logged in"")|};
        }
    }
}";

            var fixedCode = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    partial class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod()
        {
            LogUserLoggedIn();
        }

        [LoggerMessage(Level = LogLevel.Information, Message = ""User logged in"")]
        private partial void LogUserLoggedIn();
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogInformation");

            // The fixed code will have a compiler error because the source generator
            // is not running in tests. CS8795 indicates a partial method with accessibility
            // modifiers must have an implementation (provided by the source generator).
            var fixedCodeExpected = DiagnosticResult.CompilerError("CS8795")
                .WithSpan(21, 30, 21, 45)
                .WithArguments("TestNamespace.TestClass.LogUserLoggedIn()");

            await VerifyCS.VerifyCodeFixAsync(test, expected, fixedCode, fixedCodeExpected);
        }

        [TestMethod]
        public async Task CodeFix_InstanceMethod_WithParameters()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId)
        {
            {|#0:_logger.LogInformation(""User {UserId} logged in"", userId)|};
        }
    }
}";

            var fixedCode = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    partial class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId)
        {
            LogUserLoggedIn(userId);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = ""User {UserId} logged in"")]
        private partial void LogUserLoggedIn(string userId);
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogInformation");

            // The fixed code will have a compiler error because the source generator
            // is not running in tests.
            var fixedCodeExpected = DiagnosticResult.CompilerError("CS8795")
                .WithSpan(21, 30, 21, 45)
                .WithArguments("TestNamespace.TestClass.LogUserLoggedIn(string)");

            await VerifyCS.VerifyCodeFixAsync(test, expected, fixedCode, fixedCodeExpected);
        }

        [TestMethod]
        public async Task Diagnostic_AllLogLevels()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod()
        {
            {|#0:_logger.LogTrace(""Trace message"")|};
            {|#1:_logger.LogDebug(""Debug message"")|};
            {|#2:_logger.LogInformation(""Info message"")|};
            {|#3:_logger.LogWarning(""Warning message"")|};
            {|#4:_logger.LogError(""Error message"")|};
            {|#5:_logger.LogCritical(""Critical message"")|};
        }
    }
}";

            var expected = new[]
            {
                VerifyCS.Diagnostic("CTL001").WithLocation(0).WithArguments("LogTrace"),
                VerifyCS.Diagnostic("CTL001").WithLocation(1).WithArguments("LogDebug"),
                VerifyCS.Diagnostic("CTL001").WithLocation(2).WithArguments("LogInformation"),
                VerifyCS.Diagnostic("CTL001").WithLocation(3).WithArguments("LogWarning"),
                VerifyCS.Diagnostic("CTL001").WithLocation(4).WithArguments("LogError"),
                VerifyCS.Diagnostic("CTL001").WithLocation(5).WithArguments("LogCritical"),
            };

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Diagnostic_GenericILogger()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger<TestClass> _logger;

        public TestClass(ILogger<TestClass> logger)
        {
            _logger = logger;
        }

        public void TestMethod()
        {
            {|#0:_logger.LogInformation(""Message"")|};
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogInformation");

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Diagnostic_InterpolatedString_SingleParameter()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId)
        {
            {|#0:_logger.LogInformation($""User {userId} logged in"")|};
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogInformation");

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Diagnostic_InterpolatedString_MultipleParameters()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId, string action)
        {
            {|#0:_logger.LogInformation($""User {userId} performed {action}"")|};
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogInformation");

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task CodeFix_InstanceMethod_InterpolatedString()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId)
        {
            {|#0:_logger.LogInformation($""User {userId} logged in"")|};
        }
    }
}";

            var fixedCode = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    partial class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId)
        {
            LogUserLoggedIn(userId);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = ""User {UserId} logged in"")]
        private partial void LogUserLoggedIn(string userId);
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogInformation");

            var fixedCodeExpected = DiagnosticResult.CompilerError("CS8795")
                .WithSpan(21, 30, 21, 45)
                .WithArguments("TestNamespace.TestClass.LogUserLoggedIn(string)");

            await VerifyCS.VerifyCodeFixAsync(test, expected, fixedCode, fixedCodeExpected);
        }

        [TestMethod]
        public async Task CodeFix_LogClass_InterpolatedString()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId, string action)
        {
            {|#0:_logger.LogWarning($""User {userId} performed {action}"")|};
        }
    }
}";

            var fixedCode = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    partial class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string userId, string action)
        {
            Log.UserPerformed(_logger, userId, action);
        }

        private static partial class Log
        {
            [LoggerMessage(Level = LogLevel.Warning, Message = ""User {UserId} performed {Action}"")]
            public static partial void UserPerformed(ILogger logger, string userId, string action);
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogWarning");

            var fixedCodeExpected = DiagnosticResult.CompilerError("CS8795")
                .WithSpan(21, 40, 21, 53)
                .WithArguments("TestNamespace.TestClass.Log.UserPerformed(Microsoft.Extensions.Logging.ILogger, string, string)");

            await VerifyCS.VerifyCodeFixAsync(test, expected, fixedCode, fixedCodeExpected);
        }

        [TestMethod]
        public async Task Diagnostic_InterpolatedString_WithException()
        {
            var test = @"
using System;
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(string operation)
        {
            try
            {
                throw new Exception();
            }
            catch (Exception ex)
            {
                {|#0:_logger.LogError(ex, $""Error during {operation}"")|};
            }
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogError");

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }

        [TestMethod]
        public async Task Diagnostic_InterpolatedString_WithFormatting()
        {
            var test = @"
using Microsoft.Extensions.Logging;

namespace TestNamespace
{
    class TestClass
    {
        private readonly ILogger _logger;

        public TestClass(ILogger logger)
        {
            _logger = logger;
        }

        public void TestMethod(double value)
        {
            {|#0:_logger.LogInformation($""Value is {value:N2}"")|};
        }
    }
}";

            var expected = VerifyCS.Diagnostic("CTL001")
                .WithLocation(0)
                .WithArguments("LogInformation");

            await VerifyCS.VerifyAnalyzerAsync(test, expected);
        }
    }
}
