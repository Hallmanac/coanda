using System.Text;
using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Cli;

/// <summary>
/// Runs <c>coa tool-guard</c> against this process's own standard input, standard error, and
/// environment. Whatever goes wrong here, including reading the input or writing the reason, ends in
/// the exit code that denies the call.
/// </summary>
public static class ToolGuardHost
{
    public static int Run(string[] args)
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ToolGuardOutcome outcome = ToolGuard.Run(args, input, ClaudeCodeExecutor.CurrentEnvironment());
            if (outcome.Reason is { } reason)
            {
                using var error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                error.WriteLine(reason);
            }

            return outcome.ExitCode;
        }
        catch (Exception)
        {
            return ToolGuardOutcome.DenyCode;
        }
    }
}
