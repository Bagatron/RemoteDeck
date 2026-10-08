using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDeck.Protocols.Git;

/// <summary>
/// Starts a program inside a Windows pseudo console (ConPTY, Windows 10 1809 or newer), which is what lets a normal
/// command line program think it is talking to a real terminal: colors, prompts, line editing and resizing all work.
/// </summary>
internal sealed class ConPtyShellLinkFactory : IShellLinkFactory
{
    public IShellLink Start(ShellLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build < 17763)
        {
            throw new GitShellException("Local terminals need Windows 10 version 1809 or newer.");
        }

        return ConPtyLink.Start(launch);
    }

    private sealed class ConPtyLink : IShellLink
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint Infinite = 0xFFFFFFFF;

        private readonly IntPtr _console;
        private readonly IntPtr _process;
        private readonly FileStream _input;
        private readonly FileStream _output;
        private int _disposed;

        private ConPtyLink(IntPtr console, IntPtr process, FileStream input, FileStream output)
        {
            _console = console;
            _process = process;
            _input = input;
            _output = output;
        }

        public Stream Input => _input;

        public Stream Output => _output;

        public static ConPtyLink Start(ShellLaunch launch)
        {
            if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)
                || !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            {
                throw new GitShellException("Could not create the terminal pipes.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            IntPtr console = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            try
            {
                var size = new Coord((short)Math.Clamp(launch.Columns, 1, short.MaxValue), (short)Math.Clamp(launch.Rows, 1, short.MaxValue));
                var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out console);
                if (hr != 0)
                {
                    throw new GitShellException($"Could not create the terminal (error 0x{hr:X}).");
                }

                // The pseudo console now owns its ends of the pipes; ours are closed below.
                var listSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
                attributeList = Marshal.AllocHGlobal(listSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref listSize)
                    || !UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new GitShellException("Could not prepare the terminal.", new Win32Exception(Marshal.GetLastWin32Error()));
                }

                var startup = new StartupInfoEx
                {
                    StartupInfo = { cb = Marshal.SizeOf<StartupInfoEx>() },
                    AttributeList = attributeList,
                };

                var commandLine = Quote(launch.Shell.FileName)
                    + (launch.Shell.Arguments.Length > 0 ? " " + launch.Shell.Arguments : string.Empty);

                if (!CreateProcess(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                        IntPtr.Zero,
                        launch.Folder,
                        ref startup,
                        out var info))
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error());
                    throw new GitShellException($"Could not start {launch.Shell.Description}: {error.Message}", error);
                }

                CloseHandle(info.hThread);
                inputRead.Dispose();
                outputWrite.Dispose();

                var link = new ConPtyLink(
                    console,
                    info.hProcess,
                    new FileStream(inputWrite, FileAccess.Write, 1, false),
                    new FileStream(outputRead, FileAccess.Read, 4096, false));
                console = IntPtr.Zero;

                // The output pipe stays open until the pseudo console is closed, so closing it when the program
                // ends is what tells the reader the session is over.
                _ = Task.Run(() =>
                {
                    WaitForSingleObject(link._process, Infinite);
                    link.CloseConsole();
                    CloseHandle(link._process);
                });

                return link;
            }
            catch
            {
                inputWrite.Dispose();
                outputRead.Dispose();
                if (console != IntPtr.Zero)
                {
                    ClosePseudoConsole(console);
                }

                throw;
            }
            finally
            {
                inputRead.Dispose();
                outputWrite.Dispose();
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
            }
        }

        public void Resize(int columns, int rows)
        {
            if (Volatile.Read(ref _disposed) != 0 || _console == IntPtr.Zero)
            {
                return;
            }

            ResizePseudoConsole(
                _console,
                new Coord((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue)));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Ending the program first; closing the console can wait on it otherwise. The rest happens in the
            // background so closing a tab never freezes the window.
            TerminateProcess(_process, 1);
            _ = Task.Run(() =>
            {
                CloseConsole();
                _input.Dispose();
                _output.Dispose();
            });
        }

        private int _consoleClosed;

        private void CloseConsole()
        {
            if (Interlocked.Exchange(ref _consoleClosed, 1) == 0)
            {
                ClosePseudoConsole(_console);
            }
        }

        private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord
        {
            public short X;
            public short Y;

            public Coord(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
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
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, uint size);

        [DllImport("kernel32.dll")]
        private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

        [DllImport("kernel32.dll")]
        private static extern int ResizePseudoConsole(IntPtr console, Coord size);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr console);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string? applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
