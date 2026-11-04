using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// A session process started apart from the daemon, so that it outlives the daemon's exit and a
/// console interrupt aimed at the daemon: its standard input, output, and error are files, never a
/// pipe to the daemon, and it shares neither the daemon's process group nor its console. A later
/// daemon finds it again by its process id and the identity recorded at its start (its start time),
/// so a process id the system has since given another process is never taken for it.
/// </summary>
/// <remarks>
/// On macOS and Linux it is started with <c>posix_spawn</c> as the leader of a process group of its
/// own, with every signal at its default disposition and none blocked, so a terminal's interrupt or
/// hang-up, which reaches the daemon's foreground process group, does not reach it. Stopping it kills
/// its process tree and then its whole process group. Its exit is watched by its identity, and the
/// daemon process that started it also reaps it, since an exited child stays in the process table
/// until it is reaped. On Windows it is started with
/// <c>CreateProcessW</c> inheriting only its three standard handles, with no window and a hidden
/// console of its own, so a console interrupt to the daemon does not reach it, in a new process
/// group, and out of the daemon's job when the job allows it, so a job that ends its processes when
/// the daemon's handle to it closes does not end the session. A handle to the process is held while
/// it is watched, so its id is not reused meanwhile, and stopping it kills its process tree.
/// </remarks>
internal sealed class DetachedProcess : IDisposable
{
    /// <summary>How often a process this daemon cannot wait on directly is checked for its exit.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly SafeProcessHandle? handle;
    private bool exited;

    private DetachedProcess(int id, string identity, SafeProcessHandle? handle, bool started = false)
    {
        Id = id;
        Identity = identity;
        this.handle = handle;
        Started = started;
    }

    /// <summary>
    /// Whether this daemon process started it, so it is a child whose id no other process can take
    /// until this one reaps it.
    /// </summary>
    private bool Started { get; }

    public int Id { get; }

    /// <summary>The process's start time as the system reports it, which tells it apart from a later process given the same id.</summary>
    public string Identity { get; }

    /// <summary>The process's exit code, when it has exited and this daemon could read it.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>
    /// Starts <paramref name="executable"/> detached, reading <paramref name="input"/> and writing
    /// <paramref name="output"/> from its end and <paramref name="error"/>, which the caller may close
    /// once this returns. The output's own offset is moved to its end, since the process writes
    /// through the handle at that offset and a file stream keeps its append position to itself.
    /// </summary>
    public static DetachedProcess Start(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        SafeFileHandle input,
        SafeFileHandle output,
        SafeFileHandle error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        return OperatingSystem.IsWindows()
            ? Windows.Start(executable, arguments, environment, workingDirectory, input, output, error)
            : Posix.Start(executable, arguments, environment, workingDirectory, input, output, error);
    }

    /// <summary>A recorded process known to have exited, which is watched no more and never killed.</summary>
    public static DetachedProcess Exited(int id, string identity) => new(id, identity, handle: null) { exited = true };

    /// <summary>What the system holds under a recorded process id, judged by the identity recorded with it.</summary>
    public static Found Find(int id, string identity)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        if (id <= 0)
        {
            return new Found.Gone();
        }

        if (OperatingSystem.IsWindows())
        {
            return Windows.Find(id, identity);
        }

        return Posix.IdentityOf(id) switch
        {
            null => new Found.Gone(),
            (string now, _) when now != identity => new Found.Replaced(),
            _ => new DetachedProcess(id, identity, handle: null) is { HasExited: false } running
                ? new Found.Running(running)
                : new Found.Gone(),
        };
    }

    /// <summary>Whether the process has exited; once it has, it is never reported running again.</summary>
    public bool HasExited
    {
        get
        {
            if (!exited)
            {
                exited = OperatingSystem.IsWindows() ? Windows.HasExited(this) : Posix.HasExited(this);
            }

            return exited;
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (!HasExited)
        {
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>Kills the process and every process it started, unless it has already exited.</summary>
    public void KillTree()
    {
        if (HasExited)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(Id);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process ended on its own between the check and the kill.
        }

        if (!OperatingSystem.IsWindows())
        {
            // Any process of the session's group the tree walk missed, one its parent left behind among them.
            Posix.KillGroup(Id);
        }
    }

    public void Dispose() => handle?.Dispose();

    /// <summary>What a recorded process id names now.</summary>
    public abstract record Found
    {
        /// <summary>The process recorded, still running.</summary>
        public sealed record Running(DetachedProcess Process) : Found;

        /// <summary>No process has the id, or the recorded one has exited.</summary>
        public sealed record Gone : Found;

        /// <summary>The id names a process other than the one recorded, so the recorded one has exited too.</summary>
        public sealed record Replaced : Found;
    }

    private static class Posix
    {
        private const string Libc = "libc";
        private const int NoHang = 1;
        private const int SeekEnd = 2;
        private const int Kill = 9;
        private const short SetProcessGroup = 0x02;
        private const short SetSignalDefaults = 0x04;
        private const short SetSignalMask = 0x08;

        /// <summary>Larger than <c>posix_spawnattr_t</c>, <c>posix_spawn_file_actions_t</c>, and <c>sigset_t</c> on every supported platform.</summary>
        private const int OpaqueSize = 1024;

        private static readonly Lazy<bool> Resolver = new(() =>
        {
            // glibc's libc.so is a linker script dlopen cannot load; its library is libc.so.6.
            NativeLibrary.SetDllImportResolver(typeof(Posix).Assembly, (name, _, _) =>
                name == Libc && OperatingSystem.IsLinux() ? NativeLibrary.Load("libc.so.6") : IntPtr.Zero);
            return true;
        });

        public static DetachedProcess Start(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            string workingDirectory,
            SafeFileHandle input,
            SafeFileHandle output,
            SafeFileHandle error)
        {
            _ = Resolver.Value;
            IntPtr actions = Marshal.AllocHGlobal(OpaqueSize);
            IntPtr attributes = Marshal.AllocHGlobal(OpaqueSize);
            IntPtr signals = Marshal.AllocHGlobal(OpaqueSize);
            IntPtr argv = Strings([executable, .. arguments]);
            IntPtr envp = Strings([.. environment.Select(e => $"{e.Key}={e.Value}")]);
            bool actionsReady = false;
            bool attributesReady = false;
            bool inputHeld = false;
            bool outputHeld = false;
            bool errorHeld = false;
            try
            {
                input.DangerousAddRef(ref inputHeld);
                output.DangerousAddRef(ref outputHeld);
                error.DangerousAddRef(ref errorHeld);
                if (lseek((int)output.DangerousGetHandle(), 0, SeekEnd) < 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                Check(posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
                actionsReady = true;
                Check(posix_spawnattr_init(attributes), "posix_spawnattr_init");
                attributesReady = true;
                Check(posix_spawn_file_actions_adddup2(actions, (int)input.DangerousGetHandle(), 0), "posix_spawn_file_actions_adddup2");
                Check(posix_spawn_file_actions_adddup2(actions, (int)output.DangerousGetHandle(), 1), "posix_spawn_file_actions_adddup2");
                Check(posix_spawn_file_actions_adddup2(actions, (int)error.DangerousGetHandle(), 2), "posix_spawn_file_actions_adddup2");
                Check(posix_spawn_file_actions_addchdir_np(actions, workingDirectory), "posix_spawn_file_actions_addchdir_np");
                Check(posix_spawnattr_setpgroup(attributes, 0), "posix_spawnattr_setpgroup");

                // The daemon's runtime ignores SIGPIPE and may block others; the session starts with neither.
                _ = sigemptyset(signals);
                int stop = OperatingSystem.IsMacOS() ? 17 : 19;
                foreach (int signal in Enumerable.Range(1, 31).Where(s => s != Kill && s != stop))
                {
                    _ = sigaddset(signals, signal);
                }

                Check(posix_spawnattr_setsigdefault(attributes, signals), "posix_spawnattr_setsigdefault");
                _ = sigemptyset(signals);
                Check(posix_spawnattr_setsigmask(attributes, signals), "posix_spawnattr_setsigmask");
                Check(posix_spawnattr_setflags(attributes, SetProcessGroup | SetSignalDefaults | SetSignalMask), "posix_spawnattr_setflags");

                int failed = posix_spawn(out int pid, executable, actions, attributes, argv, envp);
                if (failed != 0)
                {
                    throw new Win32Exception(failed);
                }

                // Read while the process cannot yet have been reaped, so even one that exits at once has its identity.
                string identity = IdentityOf(pid)?.Identity ?? string.Empty;
                return new DetachedProcess(pid, identity, handle: null, started: true);
            }
            finally
            {
                if (attributesReady)
                {
                    _ = posix_spawnattr_destroy(attributes);
                }

                if (actionsReady)
                {
                    _ = posix_spawn_file_actions_destroy(actions);
                }

                Release(input, inputHeld);
                Release(output, outputHeld);
                Release(error, errorHeld);
                Marshal.FreeHGlobal(actions);
                Marshal.FreeHGlobal(attributes);
                Marshal.FreeHGlobal(signals);
                Free(argv);
                Free(envp);
            }
        }

        public static bool HasExited(DetachedProcess process)
        {
            _ = Resolver.Value;
            if (process.Started && Reaped(process) is { } started)
            {
                return started;
            }

            if (IdentityOf(process.Id) is not (string now, bool unreaped) || now != process.Identity)
            {
                return true;
            }

            // The recorded process is still in the table. When this daemon process is its parent all
            // the same (another executor started it), waiting on it reaps it if it has exited. When it
            // is no child to wait on, it may have been reaped since its identity was read, so the
            // identity is read once more.
            return Reaped(process)
                ?? (unreaped || IdentityOf(process.Id) is not (string again, false) || again != process.Identity);
        }

        /// <summary>True when waiting reaped the process, false when it is a running child, null when it is no child to wait on.</summary>
        private static bool? Reaped(DetachedProcess process)
        {
            int reaped = waitpid(process.Id, out int status, NoHang);
            if (reaped == process.Id)
            {
                process.ExitCode = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
                return true;
            }

            return reaped == 0 ? false : null;
        }

        /// <summary>Kills the process group the session's process leads.</summary>
        public static void KillGroup(int leader)
        {
            _ = Resolver.Value;
            _ = kill(-leader, Kill);
        }

        /// <summary>The process's start time as the system records it, and whether it has exited unreaped; null when there is no such process.</summary>
        public static (string Identity, bool Exited)? IdentityOf(int pid)
        {
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    // Fields after the command, which is in parentheses and may hold spaces: the
                    // state is field 3, and the start time in clock ticks since boot is field 22.
                    string stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
                    string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    return fields.Length > 19 ? ($"linux:{fields[19]}", fields[0] is "Z" or "X") : null;
                }
                catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }

            try
            {
                using Process process = Process.GetProcessById(pid);
                return ($"start:{process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}", false);
            }
            catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return null;
            }
        }

        private static void Check(int result, string call)
        {
            if (result != 0)
            {
                throw new Win32Exception(result, $"{call} failed: {new Win32Exception(result).Message}");
            }
        }

        private static IntPtr Strings(IReadOnlyList<string> values)
        {
            IntPtr array = Marshal.AllocHGlobal(IntPtr.Size * (values.Count + 1));
            for (int i = 0; i < values.Count; i++)
            {
                Marshal.WriteIntPtr(array, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(values[i]));
            }

            Marshal.WriteIntPtr(array, values.Count * IntPtr.Size, IntPtr.Zero);
            return array;
        }

        private static void Free(IntPtr array)
        {
            for (int offset = 0; Marshal.ReadIntPtr(array, offset) is var value && value != IntPtr.Zero; offset += IntPtr.Size)
            {
                Marshal.ZeroFreeCoTaskMemUTF8(value);
            }

            Marshal.FreeHGlobal(array);
        }

        private static void Release(SafeHandle handle, bool held)
        {
            if (held)
            {
                handle.DangerousRelease();
            }
        }

        [DllImport(Libc, SetLastError = true)]
        private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr fileActions, IntPtr attributes, IntPtr argv, IntPtr envp);

        [DllImport(Libc)]
        private static extern int posix_spawn_file_actions_init(IntPtr actions);

        [DllImport(Libc)]
        private static extern int posix_spawn_file_actions_destroy(IntPtr actions);

        [DllImport(Libc)]
        private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int descriptor, int target);

        [DllImport(Libc)]
        private static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Libc)]
        private static extern int posix_spawnattr_init(IntPtr attributes);

        [DllImport(Libc)]
        private static extern int posix_spawnattr_destroy(IntPtr attributes);

        [DllImport(Libc)]
        private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

        [DllImport(Libc)]
        private static extern int posix_spawnattr_setpgroup(IntPtr attributes, int group);

        [DllImport(Libc)]
        private static extern int posix_spawnattr_setsigdefault(IntPtr attributes, IntPtr signals);

        [DllImport(Libc)]
        private static extern int posix_spawnattr_setsigmask(IntPtr attributes, IntPtr signals);

        [DllImport(Libc)]
        private static extern int sigemptyset(IntPtr signals);

        [DllImport(Libc)]
        private static extern int sigaddset(IntPtr signals, int signal);

        [DllImport(Libc, SetLastError = true)]
        private static extern long lseek(int descriptor, long offset, int whence);

        [DllImport(Libc, SetLastError = true)]
        private static extern int waitpid(int pid, out int status, int options);

        [DllImport(Libc, SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }

    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        private const int StartfUseStdHandles = 0x100;
        private const uint CreateNewProcessGroup = 0x200;
        private const uint CreateUnicodeEnvironment = 0x400;
        private const uint ExtendedStartupInfoPresent = 0x80000;
        private const uint CreateBreakawayFromJob = 0x01000000;
        private const uint CreateNoWindow = 0x08000000;
        private const int ErrorAccessDenied = 5;
        private const uint FileEnd = 2;
        private const uint WaitObject0 = 0;
        private const int StillActive = 259;
        private const uint Synchronize = 0x00100000;
        private const uint ProcessTerminate = 0x0001;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private static readonly IntPtr HandleList = 0x20002;

        public static DetachedProcess Start(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            string workingDirectory,
            SafeFileHandle input,
            SafeFileHandle output,
            SafeFileHandle error)
        {
            SafeFileHandle[] standard = [input, output, error];
            bool[] held = new bool[standard.Length];
            IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * standard.Length);
            IntPtr block = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            IntPtr list = IntPtr.Zero;
            bool listReady = false;
            try
            {
                for (int i = 0; i < standard.Length; i++)
                {
                    standard[i].DangerousAddRef(ref held[i]);
                    Marshal.WriteIntPtr(handles, i * IntPtr.Size, standard[i].DangerousGetHandle());
                }

                if (!SetFilePointerEx(output, 0, IntPtr.Zero, FileEnd))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                // Only the three standard handles are inherited, whatever else the daemon holds inheritable.
                IntPtr size = IntPtr.Zero;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                list = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                listReady = true;
                if (!UpdateProcThreadAttribute(list, 0, HandleList, handles, IntPtr.Size * standard.Length, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startup = new StartupInfoEx();
                startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
                startup.StartupInfo.dwFlags = StartfUseStdHandles;
                startup.StartupInfo.hStdInput = input.DangerousGetHandle();
                startup.StartupInfo.hStdOutput = output.DangerousGetHandle();
                startup.StartupInfo.hStdError = error.DangerousGetHandle();
                startup.lpAttributeList = list;
                uint flags = CreateUnicodeEnvironment | CreateNoWindow | CreateNewProcessGroup | ExtendedStartupInfoPresent;
                string commandLine = CommandLine(executable, arguments);
                ProcessInformation started;
                if (!CreateProcessW(executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags | CreateBreakawayFromJob, block, workingDirectory, ref startup, out started))
                {
                    // A job that does not allow breaking away refuses the flag; the session then starts inside it.
                    int refused = Marshal.GetLastWin32Error();
                    if (refused != ErrorAccessDenied
                        || !CreateProcessW(executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags, block, workingDirectory, ref startup, out started))
                    {
                        throw new Win32Exception(refused == ErrorAccessDenied ? Marshal.GetLastWin32Error() : refused);
                    }
                }

                _ = CloseHandle(started.hThread);
                var process = new SafeProcessHandle(started.hProcess, ownsHandle: true);
                return new DetachedProcess(started.dwProcessId, IdentityOf(process) ?? string.Empty, process);
            }
            finally
            {
                if (listReady)
                {
                    DeleteProcThreadAttributeList(list);
                }

                if (list != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(list);
                }

                for (int i = 0; i < standard.Length; i++)
                {
                    if (held[i])
                    {
                        standard[i].DangerousRelease();
                    }
                }

                Marshal.FreeHGlobal(handles);
                Marshal.FreeHGlobal(block);
            }
        }

        public static Found Find(int id, string identity)
        {
            var process = new SafeProcessHandle(OpenProcess(Synchronize | ProcessTerminate | ProcessQueryLimitedInformation, false, id), ownsHandle: true);
            if (process.IsInvalid)
            {
                // A process this user may not open is not one the daemon started.
                int refused = Marshal.GetLastWin32Error();
                process.Dispose();
                return refused == ErrorAccessDenied ? new Found.Replaced() : new Found.Gone();
            }

            var found = new DetachedProcess(id, identity, process);
            if (IdentityOf(process) != identity)
            {
                found.Dispose();
                return new Found.Replaced();
            }

            if (found.HasExited)
            {
                found.Dispose();
                return new Found.Gone();
            }

            return new Found.Running(found);
        }

        public static bool HasExited(DetachedProcess process)
        {
            if (process.handle is not { IsInvalid: false } held)
            {
                return true;
            }

            // The exit code is set as soon as the process begins to exit, while the handle is signaled
            // only once its teardown ends, so a process whose code is set has exited even though a
            // wait on it would still time out. A process can exit with STILL_ACTIVE's own value, so
            // that code alone is settled by the wait.
            if (GetExitCodeProcess(held, out int code) && code != StillActive)
            {
                process.ExitCode = code;
                return true;
            }

            if (WaitForSingleObject(held, 0) != WaitObject0)
            {
                return false;
            }

            process.ExitCode = GetExitCodeProcess(held, out code) ? code : null;
            return true;
        }

        private static string? IdentityOf(SafeProcessHandle process) =>
            GetProcessTimes(process, out long created, out _, out _, out _)
                ? $"created:{created.ToString(CultureInfo.InvariantCulture)}"
                : null;

        /// <summary>The environment block: each variable as name=value, sorted by name ignoring case, each ended by a null, the block by another.</summary>
        private static string EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
        {
            var block = new StringBuilder();
            foreach ((string name, string value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                block.Append(name).Append('=').Append(value).Append('\0');
            }

            return block.Append('\0').ToString();
        }

        /// <summary>The command line the C runtime parses back into exactly these arguments.</summary>
        internal static string CommandLine(string executable, IReadOnlyList<string> arguments)
        {
            var line = new StringBuilder();
            Append(line, executable);
            foreach (string argument in arguments)
            {
                line.Append(' ');
                Append(line, argument);
            }

            return line.ToString();
        }

        private static void Append(StringBuilder line, string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                line.Append(argument);
                return;
            }

            line.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                // Backslashes are literal unless they precede a quote, so only those are doubled.
                line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
                backslashes = 0;
                line.Append(c);
            }

            line.Append('\\', backslashes * 2).Append('"');
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, IntPtr newPosition, uint method);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out int code);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    }
}
