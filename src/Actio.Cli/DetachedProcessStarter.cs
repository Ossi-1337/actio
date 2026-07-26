using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Actio.Cli;

internal static class DetachedProcessStarter
{
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint DetachedProcess = 0x00000008;
    private const int AccessDenied = 5;

    public static DetachedProcessHandle Start(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows())
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Detached process could not be started.");
            process.StandardInput.Close();
            return new DetachedProcessHandle(process, CapturesOutput: true);
        }

        return StartWindows(startInfo);
    }

    internal static string BuildWindowsCommandLine(ProcessStartInfo startInfo)
    {
        var command = new StringBuilder();
        AppendWindowsArgument(command, startInfo.FileName);
        foreach (var argument in startInfo.ArgumentList)
        {
            command.Append(' ');
            AppendWindowsArgument(command, argument);
        }

        return command.ToString();
    }

    private static DetachedProcessHandle StartWindows(ProcessStartInfo startInfo)
    {
        var environment = BuildEnvironmentBlock(startInfo);
        var environmentPointer = Marshal.StringToHGlobalUni(environment);
        try
        {
            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>()
            };
            var commandLine = new StringBuilder(BuildWindowsCommandLine(startInfo));
            var baseFlags = CreateNewProcessGroup |
                CreateUnicodeEnvironment |
                DetachedProcess;
            var flags = CreateBreakawayFromJob |
                baseFlags;
            var created = CreateProcess(
                startInfo.FileName,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                flags,
                environmentPointer,
                startInfo.WorkingDirectory,
                ref startupInfo,
                out var processInformation);
            var errorCode = created ? 0 : Marshal.GetLastWin32Error();
            if (!created && errorCode == AccessDenied)
            {
                commandLine = new StringBuilder(BuildWindowsCommandLine(startInfo));
                created = CreateProcess(
                    startInfo.FileName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    baseFlags,
                    environmentPointer,
                    startInfo.WorkingDirectory,
                    ref startupInfo,
                    out processInformation);
                errorCode = created ? 0 : Marshal.GetLastWin32Error();
            }

            if (!created)
            {
                throw new Win32Exception(
                    errorCode,
                    "Detached Actio web worker could not be created.");
            }

            try
            {
                return new DetachedProcessHandle(
                    Process.GetProcessById(processInformation.ProcessId),
                    CapturesOutput: false);
            }
            finally
            {
                CloseHandle(processInformation.ThreadHandle);
                CloseHandle(processInformation.ProcessHandle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environmentPointer);
        }
    }

    private static string BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var environment = new StringBuilder();
        foreach (var variable in startInfo.Environment.OrderBy(
            variable => variable.Key,
            StringComparer.OrdinalIgnoreCase))
        {
            environment.Append(variable.Key);
            environment.Append('=');
            environment.Append(variable.Value);
            environment.Append('\0');
        }

        environment.Append('\0');
        return environment.ToString();
    }

    private static void AppendWindowsArgument(StringBuilder command, string argument)
    {
        if (argument.Length > 0 &&
            !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            command.Append(argument);
            return;
        }

        command.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                command.Append('\\', (backslashes * 2) + 1);
                command.Append('"');
                backslashes = 0;
                continue;
            }

            command.Append('\\', backslashes);
            command.Append(character);
            backslashes = 0;
        }

        command.Append('\\', backslashes * 2);
        command.Append('"');
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateProcessW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }
}

internal sealed record DetachedProcessHandle(
    Process Process,
    bool CapturesOutput);
