using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Encoder127c.Platform;

// All calls, including disposal, run on the window's UI thread.
internal sealed class TaskbarProgress : IDisposable
{
    private readonly Window _window;
    private readonly uint _taskbarButtonCreated;
    private ITaskbarList3? _taskbar;
    private IntPtr _handle;
    private ProgressState _state;
    private ulong _value;
    private bool _disposed;
    private readonly MacDockProgress? _macDockProgress;

    public TaskbarProgress(Window window)
    {
        _window = window;
        if (OperatingSystem.IsMacOS())
            _macDockProgress = new MacDockProgress();
        if (OperatingSystem.IsWindows())
        {
            _taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
            if (_taskbarButtonCreated != 0)
                Win32Properties.AddWndProcHookCallback(window, WndProc);
        }
    }

    public void ShowIndeterminate() => Update(ProgressState.Indeterminate);

    public void SetValue(double percentage) =>
        Update(ProgressState.Normal, (ulong)(Math.Clamp(double.IsFinite(percentage) ? percentage : 0, 0, 100) * 100));

    public void Clear() => Update(ProgressState.None);

    private void Update(ProgressState state, ulong value = 0)
    {
        if (_disposed) return;
        _state = state;
        _value = value;
        Apply();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && _taskbarButtonCreated != 0 && msg == _taskbarButtonCreated)
        {
            // Wait for the shell's button creation notification before using ITaskbarList3.
            // This also restores the current progress after Explorer restarts.
            _handle = hWnd;
            Apply();
        }
        return IntPtr.Zero;
    }

    private void Apply()
    {
        if (OperatingSystem.IsMacOS())
        {
            _macDockProgress?.Update(_state == ProgressState.Normal ? _value / 100d : null);
            return;
        }
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero) return;
        try
        {
            if (_taskbar is null)
            {
                if (_state == ProgressState.None) return;
                _taskbar = (ITaskbarList3)new TaskbarList();
                _taskbar.HrInit();
            }
            _taskbar.SetProgressState(_handle, _state);
            if (_state == ProgressState.Normal)
                _taskbar.SetProgressValue(_handle, _value, 10000);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or NotSupportedException)
        {
            // Shell integration must never interrupt encoding; retry on the next update.
            Debug.WriteLine($"Taskbar progress unavailable: {exception.Message}");
            ReleaseTaskbar();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
        _macDockProgress?.Dispose();
        if (OperatingSystem.IsWindows())
        {
            if (_taskbarButtonCreated != 0)
                Win32Properties.RemoveWndProcHookCallback(_window, WndProc);
            ReleaseTaskbar();
        }
        _handle = IntPtr.Zero;
    }

    [SupportedOSPlatform("windows")]
    private void ReleaseTaskbar()
    {
        if (_taskbar is null) return;
        Marshal.ReleaseComObject(_taskbar);
        _taskbar = null;
    }

    private enum ProgressState : uint { None = 0, Indeterminate = 1, Normal = 2 }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList { }

    // Include the inherited methods in native vtable order. HRESULT failures become COMException.
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, ProgressState state);
    }
}
