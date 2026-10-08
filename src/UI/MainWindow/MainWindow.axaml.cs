using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Encoder127c.Diagnostics;
using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Services;
using Encoder127c.Encoding.Validation;
using Encoder127c.Encoders.Fdkaac.Services;
using Encoder127c.Encoders.Ffmpeg.Services;
using Encoder127c.Settings;
using Encoder127c.Platform.Power;
using Encoder127c.Platform;

namespace Encoder127c;

public partial class MainWindow : Window
{
    private readonly IFfmpegManager _ffmpegManager;
    private readonly IFdkaacManager _fdkaacManager;
    private readonly IVideoEncodingRequestValidator _requestValidator;
    private readonly EncodingSession _session;
    private bool _closeRequested;
    private bool _stopDialogOpen;
    private bool _hasEncodingProgress;
    private string? _ffmpegExecutable;
    private string? _fdkaacExecutable;
    private bool _isPreparingEncoders;
    private bool _encoderDownloadPromptAnswered;
    private bool IsEncoding => _session.IsRunning;
    private readonly SleepInhibitor _sleepInhibitor = new();
    private readonly SemaphoreSlim _sleepInhibitorGate = new(1, 1);
    private bool _isClosed;
    private readonly TaskbarProgress _taskbarProgress;
    private bool _encodingControlsEnabled = true;
    private LogWindow? _logWindow;
    private bool _isDetailedSettingsExpanded;
    private readonly BoundedLogBuffer _logBuffer = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly EncodingQueue _queue = new();
    private EncodingQueueItem? _queueDragItem;
    private EncodingQueueItem? _queueSelectionAnchor;
    private IPointer? _queueDragPointer;
    private Point _queueDragStart;
    private Point _queueDragPosition;
    private bool _queueDragStarted;
    private int _queueInsertionIndex = -1;
    private readonly DispatcherTimer _queueDragTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly CommonSettingsState _settings = new();

    public ObservableCollection<EncodingQueueItem> EncodingQueue => _queue.Items;

    public MainWindow() : this(
        FfmpegServiceFactory.CreateDefault(),
        VideoEncodingServices.CreateDefault())
    {
    }

    internal MainWindow(
        IFfmpegManager ffmpegManager,
        VideoEncodingServices videoEncodingServices)
    {
        _ffmpegManager = ffmpegManager;
        _fdkaacManager = videoEncodingServices.FdkaacManager;
        _requestValidator = videoEncodingServices.RequestValidator;
        _session = new EncodingSession(_requestValidator, videoEncodingServices.Encoder);
        _session.StatusChanged += SetStatus;
        _session.ItemStarted += (_, _, message) =>
        {
            _hasEncodingProgress = false;
            ShowIndeterminateProgress(message);
        };
        _session.ProgressChanged += UpdateEncodingProgress;
        InitializeComponent();
        _taskbarProgress = new TaskbarProgress(this);
        Title = $"127c-encoder v{GetApplicationVersion()}";
        DataContext = this;
        QueueHeader.LayoutUpdated += (_, _) => UpdateQueueColumnWidths();
        DragDrop.SetAllowDrop(QueueDropBorder, true);
        DragDrop.AddDragOverHandler(QueueDropBorder, QueueDragOver);
        DragDrop.AddDropHandler(QueueDropBorder, QueueDrop);
        // When the queue has items, the ListBox covers the drop border and can
        // consume the routed event before it reaches the border.
        DragDrop.SetAllowDrop(QueueListBox, true);
        DragDrop.AddDragOverHandler(QueueListBox, QueueDragOver);
        DragDrop.AddDropHandler(QueueListBox, QueueDrop);
        QueueListBox.AddHandler(KeyDownEvent, QueueKeyDown, RoutingStrategies.Tunnel);
        QueueListBox.AddHandler(PointerPressedEvent, QueuePointerPressed, RoutingStrategies.Tunnel);
        QueueListBox.AddHandler(PointerMovedEvent, QueuePointerMoved, RoutingStrategies.Tunnel);
        QueueListBox.AddHandler(PointerReleasedEvent, QueuePointerReleased, RoutingStrategies.Tunnel);
        QueueListBox.PointerCaptureLost += (_, _) => EndQueueDrag();
        _queueDragTimer.Tick += (_, _) => UpdateQueueDragPreview(autoScroll: true);
        ApplyDefaultSettings();
        RestoreSettings();
        LinuxDesktopMenuItem.IsVisible = OperatingSystem.IsLinux();
        Opened += async (_, _) =>
        {
            await CheckLinuxDesktopIntegrationAsync();
            CheckEncoderAvailability(this, EventArgs.Empty);
        };
        Closing += HandleClosing;
        _logTimer.Tick += (_, _) => FlushLog();
        Closed += async (_, _) =>
        {
            _isClosed = true;
            _taskbarProgress.Dispose();
            _logWindow?.Close();
            _session.Stop();
            await UpdateSleepInhibitionAsync();
            _logTimer.Stop();
            EndQueueDrag();
        };
        UpdateQueueUi();
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeRequested)
        {
            e.Cancel = true;
            return;
        }

        if (IsEncoding)
        {
            e.Cancel = true;
            _closeRequested = true;
            var completion = _session.Completion;
            try
            {
                if (!await StopEncodingAsync())
                    return;

                await completion;
            }
            finally
            {
                _closeRequested = false;
            }

            Close();
            return;
        }

        SaveSettings();
    }
}
