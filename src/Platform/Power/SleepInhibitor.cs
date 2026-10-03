using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Encoder127c.Platform.Power;

// Callers serialize changes; each request lasts only for the current encoding batch.
internal sealed class SleepInhibitor
{
    private SafeFileHandle? _windowsRequest;
    private uint? _macAssertion;
    private Process? _linuxProcess;

    public async Task StartAsync()
    {
        if (_windowsRequest is not null || _macAssertion is not null || _linuxProcess is not null)
            return;

        if (OperatingSystem.IsWindows())
        {
            var reason = Marshal.StringToHGlobalUni("127c-encoder: encoding in progress");
            try
            {
                var context = new ReasonContext { Flags = 1, Reason = new ReasonUnion { SimpleReason = reason } };
                var handle = PowerCreateRequest(ref context);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error);
                }
                if (!PowerSetRequest(handle, 1)) // PowerRequestSystemRequired
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error);
                }
                _windowsRequest = handle;
            }
            finally { Marshal.FreeHGlobal(reason); }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", 0x08000100);
            var name = CFStringCreateWithCString(IntPtr.Zero, "127c-encoder: encoding in progress", 0x08000100);
            try
            {
                var result = IOPMAssertionCreateWithName(type, 255, name, out var assertion);
                if (result != 0)
                    throw new InvalidOperationException($"IOPMAssertionCreateWithName: 0x{result:X8}");
                _macAssertion = assertion;
            }
            finally
            {
                if (type != IntPtr.Zero) CFRelease(type);
                if (name != IntPtr.Zero) CFRelease(name);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var startInfo = new ProcessStartInfo("systemd-inhibit")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "--what=sleep:idle", "--mode=block", "--who=127c-encoder",
                "--why=Encoding in progress", "--", "/bin/sh", "-c", "echo ready; exec cat >/dev/null" })
                startInfo.ArgumentList.Add(argument);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("systemd-inhibit did not start.");
            _linuxProcess = process;
            // The command runs only after logind grants the lock. EOF releases it,
            // including when the parent application terminates unexpectedly.
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
                if (ready != "ready")
                    throw new InvalidOperationException((await errors.WaitAsync(TimeSpan.FromSeconds(1))).Trim());
            }
            catch
            {
                Stop();
                throw;
            }
        }
        else
            throw new PlatformNotSupportedException("Sleep prevention is unavailable on this OS.");
    }

    public void Stop()
    {
        if (_windowsRequest is { } handle)
        {
            _windowsRequest = null;
            PowerClearRequest(handle, 1);
            handle.Dispose();
        }
        if (_macAssertion is { } assertion)
        {
            _macAssertion = null;
            IOPMAssertionRelease(assertion);
        }
        if (_linuxProcess is { } process)
        {
            _linuxProcess = null;
            // No tree kill: closing stdin lets cat and its inhibitor exit normally.
            process.StandardInput.Close();
            process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext { public uint Version; public uint Flags; public ReasonUnion Reason; }
    [StructLayout(LayoutKind.Explicit)]
    private struct ReasonUnion
    {
        [FieldOffset(0)] public IntPtr SimpleReason;
        [FieldOffset(0)] public DetailedReason Detailed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DetailedReason { public IntPtr Module; public uint Id; public uint Count; public IntPtr Strings; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle handle, int type);
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint assertion);
    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint assertion);
}
