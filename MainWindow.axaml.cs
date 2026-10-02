using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Encoder127c.Diagnostics;
using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Services;
using Encoder127c.Encoding.Validation;
using Encoder127c.Fdkaac.Services;
using Encoder127c.Ffmpeg.Services;
using Encoder127c.Settings;
using Encoder127c.Tools;

namespace Encoder127c;

public partial class MainWindow : Window
{
    private readonly IFfmpegManager _ffmpegManager;
    private readonly IFdkaacManager _fdkaacManager;
    private readonly IVideoEncodingRequestValidator _requestValidator;
    private readonly IVideoEncoder _videoEncoder;
    private CancellationTokenSource? _encodingCancellation;
    private string? _ffmpegExecutable;
    private string? _fdkaacExecutable;
    private bool _isPreparingEncoders;
    private bool _isEncoding;
    private bool _encodingControlsEnabled = true;
    private bool _isLogVisible;
    private readonly BoundedLogBuffer _logBuffer = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private EncodingQueueItem? _selectedItem;
    private EncodingQueueItem? _queueDragItem;
    private IPointer? _queueDragPointer;
    private Point _queueDragStart;
    private Point _queueDragPosition;
    private bool _queueDragStarted;
    private int _queueInsertionIndex = -1;
    private readonly DispatcherTimer _queueDragTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private AudioGainOptions _audioGainOptions = new();
    private decimal _defaultVideoMaxBitrate = 2000;
    private decimal _defaultVideoBufferSize = 4000;

    public ObservableCollection<EncodingQueueItem> EncodingQueue { get; } = [];

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
        _videoEncoder = videoEncodingServices.Encoder;
        InitializeComponent();
        Title = $"127c-encoder v{GetApplicationVersion()}";
        DataContext = this;
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
        QueueDropBorder.AddHandler(PointerReleasedEvent, EmptyQueuePointerReleased,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        ApplyDefaultSettings();
        RestoreSettings();
        Opened += CheckEncoderAvailability;
        Closing += SaveSettings;
        _logTimer.Tick += (_, _) => FlushLog();
        Opened += (_, _) => _logTimer.Start();
        Closed += (_, _) =>
        {
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

    private async void PickInputFiles(object? sender, RoutedEventArgs e) =>
        await PickInputFilesAsync();

    private async void EmptyQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left ||
            EncodingQueue.Count != 0 || !AddFilesButton.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        await PickInputFilesAsync();
    }

    private async void OpenFilesFromMenu(object? sender, EventArgs e) =>
        await PickInputFilesAsync();

    private void CloseFromMenu(object? sender, EventArgs e) => Close();

    private async void AnalyzeQueueItemGain(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding || _isPreparingEncoders ||
            sender is not Control { DataContext: EncodingQueueItem item } || !EncodingQueue.Contains(item))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_ffmpegExecutable))
        {
            await ShowMessageDialogAsync("게인을 측정하려면 FFmpeg를 먼저 다운로드하세요.");
            return;
        }

        var dialog = new AudioGainDialog(_ffmpegExecutable, item.Path, _audioGainOptions);
        var result = await dialog.ShowDialog<decimal?>(this);
        _audioGainOptions = dialog.Options;
        if (result is { } gain)
        {
            if (DynamicAudioNormalizationCheckBox.IsChecked == true &&
                await ShowConfirmationDialogAsync(
                    "노멀라이징이 체크되어 있습니다. 해제할까요?",
                    "볼륨이 너무 커져 과도한 클리핑이 발생할 수 있습니다."))
            {
                DynamicAudioNormalizationCheckBox.IsChecked = false;
            }

            AudioGainNumericUpDown.Value = gain;
        }
    }

    private async Task PickInputFilesAsync()
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || _isEncoding)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "입력 비디오 추가",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("비디오 파일")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.webm", "*.m4v"]
                },
                FilePickerFileTypes.All
            ]
        });

        AddFiles(files.Select(file => file.TryGetLocalPath()));
    }

    private void QueueDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !_isEncoding && e.DataTransfer.TryGetFiles()?.Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueDrop(object? sender, DragEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        AddFiles(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()) ?? []);
        e.Handled = true;
    }

    private void AddFiles(IEnumerable<string?> paths)
    {
        var existingPaths = new HashSet<string>(
            EncodingQueue.Select(item => item.Path),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var added = 0;
        var requeued = 0;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                if (!existingPaths.Add(fullPath))
                {
                    var existingItem = EncodingQueue.First(item =>
                        string.Equals(item.Path, fullPath, OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal));
                    if (existingItem.Status == EncodingQueueStatus.Completed)
                    {
                        existingItem.ResetStatus();
                        requeued++;
                    }

                    continue;
                }

                EncodingQueue.Add(new EncodingQueueItem(fullPath));
                added++;
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                NotSupportedException or
                PathTooLongException or
                UnauthorizedAccessException or
                IOException)
            {
                // Ignore paths that cannot be represented locally.
            }
        }

        if (added > 0 || requeued > 0)
        {
            SetStatus(requeued > 0
                ? $"{added}개 파일을 추가하고 완료된 {requeued}개 파일을 다시 대기열에 넣었습니다. 총 {EncodingQueue.Count}개"
                : $"{added}개 파일을 추가했습니다. 총 {EncodingQueue.Count}개");
        }

        UpdateQueueUi();
    }

    private void QueueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedItem = QueueListBox.SelectedItem as EncodingQueueItem;
        UpdateQueueUi();
    }

    private EncodingQueueItem[] GetSelectedQueueItems() =>
        QueueListBox.SelectedItems?.OfType<EncodingQueueItem>().ToArray() ?? [];

    private EncodingQueueItem[] GetQueueActionItems(object? sender)
    {
        if (sender is not Control { DataContext: EncodingQueueItem item } || !EncodingQueue.Contains(item))
        {
            return [];
        }

        var selectedItems = GetSelectedQueueItems();
        return selectedItems.Contains(item) ? selectedItems : [item];
    }

    private void SelectAllQueueItems(object? sender, RoutedEventArgs e)
    {
        QueueListBox.SelectAll();
        QueueListBox.Focus();
    }

    private void QueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _queueDragPointer is not null)
        {
            EndQueueDrag();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && (e.KeyModifiers == KeyModifiers.Control ||
            OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Meta))
        {
            SelectAllQueueItems(sender, e);
            e.Handled = true;
        }
    }

    private void QueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(QueueListBox).Properties.IsLeftButtonPressed ||
            e.Source is not Visual source)
        {
            return;
        }

        var handle = source.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(border => border.Name == "QueueDragHandle");
        if (handle?.DataContext is not EncodingQueueItem item)
        {
            return;
        }

        e.Handled = true;
        if (_isEncoding || !_encodingControlsEnabled)
        {
            return;
        }

        _queueDragItem = item;
        _queueDragStart = _queueDragPosition = e.GetPosition(QueueListBox);
        QueueListBox.SelectedItem = item;
        QueueListBox.Focus();
        _queueDragPointer = e.Pointer;
        e.Pointer.Capture(QueueListBox);
        _queueDragTimer.Start();
    }

    private void QueuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_queueDragPointer != e.Pointer)
        {
            return;
        }

        _queueDragPosition = e.GetPosition(QueueListBox);
        UpdateQueueDragPreview();
        e.Handled = true;
    }

    private void UpdateQueueDragPreview(bool autoScroll = false)
    {
        _queueInsertionIndex = -1;
        QueueInsertionLine.IsVisible = false;
        if (_queueDragItem is not { } item || _isEncoding || !_encodingControlsEnabled)
        {
            return;
        }

        if (!_queueDragStarted && Math.Abs(_queueDragPosition.Y - _queueDragStart.Y) < 4)
        {
            return;
        }
        _queueDragStarted = true;

        if (!EncodingQueue.Contains(item))
        {
            EndQueueDrag();
            return;
        }

        if (!new Rect(QueueListBox.Bounds.Size).Contains(_queueDragPosition))
        {
            return;
        }

        // Keep moving through long queues when the handle is held near an edge.
        var scroll = QueueListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (autoScroll && scroll?.TranslatePoint(default, QueueListBox) is { } scrollOrigin)
        {
            var direction = _queueDragPosition.Y < scrollOrigin.Y + 24 ? -1
                : _queueDragPosition.Y > scrollOrigin.Y + scroll.Bounds.Height - 24 ? 1 : 0;
            if (direction != 0)
            {
                scroll.Offset = new Vector(scroll.Offset.X,
                    Math.Clamp(scroll.Offset.Y + direction * 12, 0,
                        Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
                QueueListBox.UpdateLayout();
            }
        }

        Control? target = null;
        var insertAfter = false;
        foreach (var container in QueueListBox.GetRealizedContainers().OrderBy(QueueListBox.IndexFromContainer))
        {
            if (container.TranslatePoint(default, QueueListBox) is not { } origin)
            {
                continue;
            }

            target = container;
            insertAfter = _queueDragPosition.Y >= origin.Y + container.Bounds.Height / 2;
            if (!insertAfter)
            {
                break;
            }
        }

        if (target?.TranslatePoint(default, QueueInsertionOverlay) is { } lineOrigin)
        {
            _queueInsertionIndex = QueueListBox.IndexFromContainer(target) + (insertAfter ? 1 : 0);
            Canvas.SetLeft(QueueInsertionLine, lineOrigin.X + 8);
            Canvas.SetTop(QueueInsertionLine, Math.Clamp(
                lineOrigin.Y + (insertAfter ? target.Bounds.Height : 0) - 1,
                0, Math.Max(0, QueueInsertionOverlay.Bounds.Height - 2)));
            QueueInsertionLine.Width = Math.Max(0, target.Bounds.Width - 16);
            QueueInsertionLine.IsVisible = true;
        }
    }

    private void QueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_queueDragPointer == e.Pointer)
        {
            e.Handled = true;
            _queueDragPosition = e.GetPosition(QueueListBox);
            UpdateQueueDragPreview();
            var item = _queueDragItem;
            var insertionIndex = _queueInsertionIndex;
            EndQueueDrag();
            if (item is not null && insertionIndex >= 0 && !_isEncoding && _encodingControlsEnabled)
            {
                var oldIndex = EncodingQueue.IndexOf(item);
                var newIndex = insertionIndex > oldIndex ? insertionIndex - 1 : insertionIndex;
                if (oldIndex >= 0 && newIndex != oldIndex && newIndex < EncodingQueue.Count)
                {
                    EncodingQueue.Move(oldIndex, newIndex);
                    QueueListBox.SelectedItem = item;
                    UpdateQueueUi();
                }
            }
        }
    }

    private void EndQueueDrag()
    {
        _queueDragTimer.Stop();
        var pointer = _queueDragPointer;
        _queueDragPointer = null;
        _queueDragItem = null;
        _queueDragStarted = false;
        _queueInsertionIndex = -1;
        QueueInsertionLine.IsVisible = false;
        pointer?.Capture(null);
    }

    private void QueueItemContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: EncodingQueueItem item } menu)
        {
            e.Cancel = true;
            return;
        }

        if (!GetSelectedQueueItems().Contains(item))
        {
            QueueListBox.SelectedItem = item;
        }

        var menuItems = menu.Items.OfType<MenuItem>().ToArray();
        menuItems[1].IsEnabled = GetQueueActionItems(menu)
            .Any(selected => !string.IsNullOrWhiteSpace(GetQueueItemOutputDirectory(selected)));
        menuItems[2].IsEnabled = !_isEncoding && !_isPreparingEncoders && _ffmpegExecutable is not null;
        menuItems[3].IsEnabled = !_isEncoding;
        menuItems[4].IsEnabled = !_isEncoding;
        menuItems[5].InputGesture = new KeyGesture(Key.A,
            OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
    }

    private void OpenQueueItemSourceFolder(object? sender, RoutedEventArgs e)
    {
        OpenQueueDirectories(GetQueueActionItems(sender).Select(item => item.SourceDirectory));
    }

    private void OpenQueueItemOutputFolder(object? sender, RoutedEventArgs e)
    {
        OpenQueueDirectories(GetQueueActionItems(sender).Select(GetQueueItemOutputDirectory), createIfMissing: true);
    }

    private void OpenQueueDirectories(IEnumerable<string?> directories, bool createIfMissing = false)
    {
        var openedDirectories = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                if (openedDirectories.Add(fullDirectory))
                {
                    OpenDirectory(fullDirectory, createIfMissing);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                SetStatus("폴더를 열 수 없습니다.");
            }
        }
    }

    private string? GetQueueItemOutputDirectory(EncodingQueueItem item) =>
        item.OutputDirectory ?? GetOutputDirectory(item.Path).Trim();

    private void ResetQueueItemStatus(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        var items = GetQueueActionItems(sender);
        foreach (var item in items)
        {
            item.ResetStatus();
        }

        SetStatus($"{items.Length}개 파일의 상태를 초기화했습니다.");
    }

    private void RemoveQueueItem(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        RemoveQueueItems(GetQueueActionItems(sender));
    }

    private void RemoveQueueItems(EncodingQueueItem[] items)
    {
        foreach (var item in items)
        {
            EncodingQueue.Remove(item);
        }

        UpdateQueueUi();
    }

    private void RemoveSelectedFile(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        RemoveQueueItems(GetSelectedQueueItems());
    }

    private void ClearFiles(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        EncodingQueue.Clear();
        _selectedItem = null;
        SetStatus("파일 목록을 비웠습니다.");
        UpdateQueueUi();
    }

    private void MoveSelectedUp(object? sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveSelectedDown(object? sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int direction)
    {
        if (_isEncoding || _selectedItem is null || GetSelectedQueueItems().Length != 1)
        {
            return;
        }

        var oldIndex = EncodingQueue.IndexOf(_selectedItem);
        var newIndex = oldIndex + direction;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= EncodingQueue.Count)
        {
            return;
        }

        EncodingQueue.Move(oldIndex, newIndex);
        QueueListBox.SelectedItem = _selectedItem;
        UpdateQueueUi();
    }

    private async void PickOutputFolder(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || _isEncoding)
        {
            return;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "출력 폴더 선택",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            OutputDirectoryTextBox.Text = folders[0].TryGetLocalPath();
        }
    }

    private void OpenOutputFolder(object? sender, RoutedEventArgs e)
        => OpenDirectory(OutputDirectoryTextBox.Text?.Trim(), createIfMissing: true);

    private void OpenDirectory(string? directory, bool createIfMissing = false)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            SetStatus("열 폴더를 먼저 선택하세요.");
            return;
        }

        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            if (createIfMissing)
            {
                Directory.CreateDirectory(fullDirectory);
            }

            if (!Directory.Exists(fullDirectory))
            {
                SetStatus("폴더가 존재하지 않습니다.");
                return;
            }

            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(fullDirectory) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                {
                    UseShellExecute = false
                };
            if (!OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add(fullDirectory);
            }
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException or
            IOException or
            System.ComponentModel.Win32Exception)
        {
            SetStatus("폴더를 열 수 없습니다.");
        }
    }

    private void ResetSettings(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        ApplyDefaultSettings();
        SetStatus("설정을 기본값으로 되돌렸습니다.");
    }

    private async void StartEncoding(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            StopEncoding();
            return;
        }

        if (string.IsNullOrWhiteSpace(_ffmpegExecutable))
        {
            SetStatus("FFmpeg를 먼저 다운로드하세요.");
            return;
        }

        var filesToEncode = EncodingQueue
            .Where(item => item.Status != EncodingQueueStatus.Completed)
            .ToList();
        if (filesToEncode.Count == 0)
        {
            SetStatus(EncodingQueue.Count == 0
                ? "인코딩할 비디오 파일을 추가하세요."
                : "모든 파일이 이미 완료되었습니다.");
            return;
        }

        var initialValidation = _requestValidator.Validate(CreateEncodingRequest(filesToEncode[0].Path));
        if (!initialValidation.IsValid)
        {
            SetStatus(initialValidation.ErrorMessage!);
            return;
        }

        if (UseSourceDirectoryCheckBox.IsChecked != true)
        {
            var outputDirectory = Path.GetFullPath(OutputDirectoryTextBox.Text!.Trim());
            if (!Directory.Exists(outputDirectory))
            {
                var shouldCreateOutputDirectory = await ShowConfirmationDialogAsync(
                    "출력 폴더가 없습니다. 폴더를 만들까요?");
                if (!shouldCreateOutputDirectory)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(outputDirectory);
                }
                catch (Exception exception) when (exception is
                    ArgumentException or
                    NotSupportedException or
                    PathTooLongException or
                    UnauthorizedAccessException or
                    IOException or
                    System.Security.SecurityException)
                {
                    await ShowMessageDialogAsync("폴더를 만들 수 없습니다. 다른 경로를 지정해 주세요.");
                    return;
                }
            }
        }

        _isEncoding = true;
        _encodingCancellation = new CancellationTokenSource();
        SetEncodingControlsEnabled(false);
        EncodeButton.IsEnabled = true;
        EncodeButtonIcon.Icon = FluentIcons.Common.Icon.DismissCircle;
        EncodeButtonText.Text = "중지하기";
        ClearLog();
        ShowIndeterminateProgress();

        var completedCount = EncodingQueue.Count(item => item.Status == EncodingQueueStatus.Completed);
        string? finalStatus = null;
        try
        {
            foreach (var item in filesToEncode)
            {
                _encodingCancellation.Token.ThrowIfCancellationRequested();
                var itemNumber = EncodingQueue.IndexOf(item) + 1;
                var validation = _requestValidator.Validate(CreateEncodingRequest(item.Path));
                if (!validation.IsValid)
                {
                    item.Status = EncodingQueueStatus.Failed;
                    SetStatus(FormatEncodingStatus(itemNumber, "인코딩 실패", item.FileName));
                    AppendLog($"[오류] {item.FileName}: {validation.ErrorMessage}");
                    continue;
                }

                var request = validation.Request!;
                if (!TryCheckOutputDirectoryWritable(request.OutputPath, out var writeErrorMessage))
                {
                    item.Status = EncodingQueueStatus.Failed;
                    SetStatus(FormatEncodingStatus(itemNumber, "인코딩 실패", item.FileName));
                    AppendLog($"[오류] {item.FileName}: {writeErrorMessage}");
                    continue;
                }

                item.BeginEncoding(request.OutputPath);
                var startMessage = FormatEncodingStatus(itemNumber, "인코딩 시작", item.FileName);
                SetStatus(startMessage);
                AppendLog($"[시작] {item.FileName} → {Path.GetFileName(request.OutputPath)}");
                ShowIndeterminateProgress(startMessage);

                try
                {
                    var result = await _videoEncoder.EncodeAsync(
                        _ffmpegExecutable,
                        request,
                        _logBuffer,
                        new Progress<EncodingProgress>(progress => UpdateEncodingProgress(progress, itemNumber)),
                        _encodingCancellation.Token);

                    if (result.ExitCode == 0)
                    {
                        item.Status = EncodingQueueStatus.Completed;
                        completedCount++;
                        SetStatus(FormatEncodingStatus(itemNumber, "인코딩 완료", item.FileName));
                        AppendLog($"[완료] {item.FileName} → {Path.GetFileName(request.OutputPath)}");
                    }
                    else
                    {
                        item.Status = EncodingQueueStatus.Failed;
                        SetStatus(FormatEncodingStatus(itemNumber, "인코딩 실패", item.FileName));
                        AppendLog($"[오류] {item.FileName}: ffmpeg 종료 코드 {result.ExitCode}");
                    }
                }
                catch (OperationCanceledException) when (_encodingCancellation.IsCancellationRequested)
                {
                    item.Status = EncodingQueueStatus.Stopped;
                    throw;
                }
                catch (Exception exception)
                {
                    item.Status = EncodingQueueStatus.Failed;
                    SetStatus(FormatEncodingStatus(itemNumber, "인코딩 실패", item.FileName));
                    AppendLog($"[오류] {item.FileName}: {exception.Message}");
                }
            }

            finalStatus = $"[{DateTime.Now:HH:mm:ss}] 전체 인코딩 완료: {completedCount}/{EncodingQueue.Count}개";
        }
        catch (OperationCanceledException) when (_encodingCancellation.IsCancellationRequested)
        {
            finalStatus = $"[{DateTime.Now:HH:mm:ss}] 인코딩 중지: 완료된 {completedCount}개 파일은 다음 실행에서 건너뜁니다.";
            AppendLog("[중지] 현재 인코딩을 즉시 중단했습니다.");
        }
        finally
        {
            _encodingCancellation.Dispose();
            _encodingCancellation = null;
            _isEncoding = false;
            SetEncodingControlsEnabled(true);
            UpdateEncodeButton();
            HideEncodingProgress();
            if (finalStatus is not null)
            {
                SetStatus(finalStatus);
            }
        }
    }

    private void StopEncoding()
    {
        if (!_isEncoding || _encodingCancellation is null)
        {
            return;
        }

        EncodeButton.IsEnabled = false;
        SetStatus("인코딩 프로세스를 중지하는 중...");
        _encodingCancellation.Cancel();
    }

    private async void CheckEncoderAvailability(object? sender, EventArgs e)
    {
        if (_isPreparingEncoders)
        {
            return;
        }

        _isPreparingEncoders = true;
        EncodeButton.IsEnabled = false;
        DownloadEncodersButton.IsEnabled = false;
        try
        {
            _ffmpegExecutable = await _ffmpegManager.FindAvailableExecutableAsync();

            _fdkaacExecutable = await _fdkaacManager.FindAvailableExecutableAsync();

            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null || _fdkaacExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus(_ffmpegExecutable is null
                ? "FFmpeg가 없습니다. 다운로드 후 인코딩할 수 있습니다."
                : _fdkaacExecutable is null
                    ? "fdkaac가 없습니다. 인코더 다운로드 후 오디오를 인코딩할 수 있습니다."
                    : "인코더 준비 완료");
        }
        catch (Exception exception)
        {
            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null || _fdkaacExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus($"인코더 확인 오류: {exception.Message}");
        }
        finally
        {
            _isPreparingEncoders = false;
            UpdateEncodeButton();
        }
    }

    private async void DownloadEncoders(object? sender, RoutedEventArgs e)
    {
        if (_isPreparingEncoders || _isEncoding)
        {
            return;
        }

        _isPreparingEncoders = true;
        EncodeButton.IsEnabled = false;
        DownloadEncodersButton.IsEnabled = false;
        ShowIndeterminateProgress();
        ClearLog();
        try
        {
            AppendLog("[인코더 준비] FFmpeg 준비를 시작합니다.");

            _ffmpegExecutable = await _ffmpegManager.EnsureAvailableAsync(
                new EncoderPreparationProgress(message => ReportEncoderPreparation("FFmpeg", message)));

            _fdkaacExecutable = await _fdkaacManager.EnsureAvailableAsync(
                progress: new EncoderPreparationProgress(message => ReportEncoderPreparation("fdkaac", message)));
            DownloadEncodersButton.IsVisible = false;
            DownloadEncodersButton.IsEnabled = false;
            SetStatus("인코더 준비 완료");
            AppendLog("[인코더 준비] 인코딩을 시작할 수 있습니다.");
        }
        catch (Exception exception)
        {
            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null || _fdkaacExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus($"인코더 설치 오류: {exception.Message}");
            AppendLog($"[인코더 준비] 오류: {exception}");
        }
        finally
        {
            _isPreparingEncoders = false;
            UpdateEncodeButton();
            HideEncodingProgress();
        }
    }

    private VideoEncodingRequest CreateEncodingRequest(string inputPath) => new(
        inputPath,
        GetOutputDirectory(inputPath),
        GetSelectedTag(EncodingProfileComboBox) ?? DefaultEncodingPreset.DefaultEncodingProfile,
        GetSelectedTag(VideoPresetComboBox) ?? DefaultEncodingPreset.DefaultVideoPreset,
        IsSavingEncodingProfile()
            ? DefaultEncodingPreset.SavingVideoMaxBitrate
            : FormatKiloBitrate(VideoMaxBitrateNumericUpDown.Value, DefaultEncodingPreset.DefaultVideoMaxBitrate),
        IsSavingEncodingProfile()
            ? DefaultEncodingPreset.SavingVideoBufferSize
            : FormatKiloBitrate(VideoBufferSizeNumericUpDown.Value, DefaultEncodingPreset.DefaultVideoBufferSize),
        GetSelectedTag(DeinterlaceModeComboBox) ?? DefaultEncodingPreset.DefaultDeinterlaceMode,
        (AudioGainNumericUpDown.Value ?? 0).ToString("0", CultureInfo.InvariantCulture),
        DynamicAudioNormalizationCheckBox.IsChecked == true);

    private string GetOutputDirectory(string inputPath) =>
        UseSourceDirectoryCheckBox.IsChecked == true
            ? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty
            : OutputDirectoryTextBox.Text ?? string.Empty;

    private static bool TryCheckOutputDirectoryWritable(string outputPath, out string errorMessage)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            errorMessage = "출력 폴더에 파일을 쓸 수 없습니다.";
            return false;
        }

        var probeCreated = false;
        try
        {
            using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            probeCreated = true;
            File.Delete(outputPath);
            probeCreated = false;
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            if (probeCreated)
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception cleanupException) when (cleanupException is
                    ArgumentException or
                    NotSupportedException or
                    PathTooLongException or
                    UnauthorizedAccessException or
                    IOException or
                    System.Security.SecurityException)
                {
                    // Leave the original write error as the result for this item.
                }
            }

            errorMessage = "출력 폴더에 파일을 쓸 수 없습니다.";
            return false;
        }
    }

    private static string FormatKiloBitrate(decimal? value, string fallback) =>
        value is decimal kiloBitrate
            ? $"{kiloBitrate.ToString("0", CultureInfo.InvariantCulture)}k"
            : fallback;

    private static string? GetSelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private bool IsSavingEncodingProfile() =>
        string.Equals(
            GetSelectedTag(EncodingProfileComboBox),
            DefaultEncodingPreset.EncodingProfileSaving,
            StringComparison.Ordinal);

    private bool IsAudioOnlyEncodingProfile() =>
        GetSelectedTag(EncodingProfileComboBox) == DefaultEncodingPreset.EncodingProfileAudioOnly;

    private void UseSourceDirectoryChanged(object? sender, RoutedEventArgs e) =>
        UpdateOutputDirectoryControls();

    private void EncodingProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.OfType<ComboBoxItem>().Any(item =>
                item.Tag as string == DefaultEncodingPreset.EncodingProfileDefault))
        {
            CaptureDefaultVideoBitrates();
        }

        if (IsSavingEncodingProfile())
        {
            VideoMaxBitrateNumericUpDown.Value = 900;
            VideoBufferSizeNumericUpDown.Value = 900;
        }
        else
        {
            VideoMaxBitrateNumericUpDown.Value = _defaultVideoMaxBitrate;
            VideoBufferSizeNumericUpDown.Value = _defaultVideoBufferSize;
        }

        UpdateEncodingProfileControls();
    }

    private void UpdateEncodingProfileControls()
    {
        var isSavingProfile = IsSavingEncodingProfile();
        var isAudioOnlyProfile = IsAudioOnlyEncodingProfile();
        VideoPresetComboBox.IsEnabled = _encodingControlsEnabled && !isAudioOnlyProfile;
        DeinterlaceModeComboBox.IsEnabled = _encodingControlsEnabled && !isAudioOnlyProfile;
        VideoMaxBitrateNumericUpDown.IsReadOnly = isSavingProfile || isAudioOnlyProfile;
        VideoBufferSizeNumericUpDown.IsReadOnly = isSavingProfile || isAudioOnlyProfile;
        VideoMaxBitrateNumericUpDown.IsEnabled = _encodingControlsEnabled && !isSavingProfile && !isAudioOnlyProfile;
        VideoBufferSizeNumericUpDown.IsEnabled = _encodingControlsEnabled && !isSavingProfile && !isAudioOnlyProfile;
    }

    private void ApplyDefaultSettings()
    {
        _audioGainOptions = new();
        _defaultVideoMaxBitrate = 2000;
        _defaultVideoBufferSize = 4000;
        OutputDirectoryTextBox.Text = Path.GetFullPath(AppPaths.DefaultOutputDirectory);
        UseSourceDirectoryCheckBox.IsChecked = false;
        SelectComboBoxItem(EncodingProfileComboBox, DefaultEncodingPreset.DefaultEncodingProfile);
        SelectComboBoxItem(VideoPresetComboBox, "slow");
        SelectComboBoxItem(DeinterlaceModeComboBox, DefaultEncodingPreset.DefaultDeinterlaceMode);
        VideoMaxBitrateNumericUpDown.Value = 2000;
        VideoBufferSizeNumericUpDown.Value = 4000;
        AudioGainNumericUpDown.Value = 0;
        DynamicAudioNormalizationCheckBox.IsChecked = false;
    }

    private void RestoreSettings()
    {
        var settings = EncoderSettingsStore.Load();
        if (settings is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            OutputDirectoryTextBox.Text = settings.OutputDirectory;
        }

        UseSourceDirectoryCheckBox.IsChecked = settings.UseSourceDirectory;

        var encodingProfile = settings.EncodingProfile ?? DefaultEncodingPreset.DefaultEncodingProfile;
        SelectComboBoxItem(EncodingProfileComboBox, encodingProfile);
        _defaultVideoMaxBitrate = ClampToRange(
            settings.DefaultVideoMaxBitrate ?? (IsDefaultEncodingProfile(encodingProfile) ? settings.VideoMaxBitrate : null),
            1, 1_000_000, 2000);
        _defaultVideoBufferSize = ClampToRange(
            settings.DefaultVideoBufferSize ?? (IsDefaultEncodingProfile(encodingProfile) ? settings.VideoBufferSize : null),
            1, 1_000_000, 4000);

        SelectComboBoxItem(VideoPresetComboBox, settings.VideoPreset);
        SelectComboBoxItem(DeinterlaceModeComboBox, settings.DeinterlaceMode);
        if (!IsSavingEncodingProfile())
        {
            VideoMaxBitrateNumericUpDown.Value = _defaultVideoMaxBitrate;
            VideoBufferSizeNumericUpDown.Value = _defaultVideoBufferSize;
        }
        AudioGainNumericUpDown.Value = ClampToRange(settings.AudioGain, -60, 60, 0);
        DynamicAudioNormalizationCheckBox.IsChecked = settings.DynamicAudioNormalization;
        if (settings.AudioGainAnalysis is { } options)
        {
            try
            {
                options.Validate();
                _audioGainOptions = options;
            }
            catch (ArgumentException)
            {
                // Keep defaults when saved analysis conditions are invalid.
            }
        }

        UpdateEncodingProfileControls();
    }

    private void SaveSettings(object? sender, WindowClosingEventArgs e)
    {
        if (!IsSavingEncodingProfile())
        {
            CaptureDefaultVideoBitrates();
        }

        EncoderSettingsStore.Save(new EncoderSettings
        {
            OutputDirectory = OutputDirectoryTextBox.Text?.Trim(),
            UseSourceDirectory = UseSourceDirectoryCheckBox.IsChecked == true,
            EncodingProfile = GetSelectedTag(EncodingProfileComboBox),
            VideoPreset = GetSelectedTag(VideoPresetComboBox),
            DeinterlaceMode = GetSelectedTag(DeinterlaceModeComboBox),
            VideoMaxBitrate = VideoMaxBitrateNumericUpDown.Value,
            VideoBufferSize = VideoBufferSizeNumericUpDown.Value,
            DefaultVideoMaxBitrate = _defaultVideoMaxBitrate,
            DefaultVideoBufferSize = _defaultVideoBufferSize,
            AudioGain = AudioGainNumericUpDown.Value,
            DynamicAudioNormalization = DynamicAudioNormalizationCheckBox.IsChecked == true,
            AudioGainAnalysis = _audioGainOptions
        });
    }

    private void CaptureDefaultVideoBitrates()
    {
        _defaultVideoMaxBitrate = ClampToRange(VideoMaxBitrateNumericUpDown.Value, 1, 1_000_000, 2000);
        _defaultVideoBufferSize = ClampToRange(VideoBufferSizeNumericUpDown.Value, 1, 1_000_000, 4000);
    }

    private static bool IsDefaultEncodingProfile(string profile) =>
        string.Equals(profile, DefaultEncodingPreset.EncodingProfileDefault, StringComparison.Ordinal);

    private static decimal ClampToRange(decimal? value, decimal minimum, decimal maximum, decimal fallback) =>
        value is decimal number && number >= minimum && number <= maximum ? number : fallback;

    private async Task<bool> ShowConfirmationDialogAsync(string message, string? detail = null)
    {
        var dialog = CreateDialog(
            "확인",
            message,
            [
                ("예", true),
                ("아니오", false)
            ],
            detail);

        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowMessageDialogAsync(string message)
    {
        var dialog = CreateDialog("오류", message, [("확인", true)]);
        await dialog.ShowDialog<bool>(this);
    }

    private static Window CreateDialog(
        string title,
        string message,
        (string Text, bool Result)[] buttons,
        string? detail = null)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };

        foreach (var (text, result) in buttons)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 80
            };
            button.Click += (_, _) => dialog.Close(result);
            buttonPanel.Children.Add(button);
        }

        var messagePanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        if (detail is not null)
        {
            messagePanel.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                messagePanel,
                buttonPanel
            }
        };

        return dialog;
    }

    private static void SelectComboBoxItem(ComboBox comboBox, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var item = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
        if (item is not null)
        {
            comboBox.SelectedItem = item;
        }
    }

    private void UpdateQueueUi()
    {
        DropHintPanel.IsVisible = EncodingQueue.Count == 0;
        var selectionCount = GetSelectedQueueItems().Length;
        var hasSelection = selectionCount > 0;
        var hasSingleSelection = selectionCount == 1 && _selectedItem is not null;
        RemoveSelectedButton.IsEnabled = !_isEncoding && hasSelection;
        MoveUpButton.IsEnabled = !_isEncoding && hasSingleSelection && EncodingQueue.IndexOf(_selectedItem!) > 0;
        MoveDownButton.IsEnabled = !_isEncoding && hasSingleSelection && EncodingQueue.IndexOf(_selectedItem!) < EncodingQueue.Count - 1;
    }

    private void SetEncodingControlsEnabled(bool isEnabled)
    {
        if (!isEnabled)
        {
            EndQueueDrag();
        }
        _encodingControlsEnabled = isEnabled;
        AddFilesButton.IsEnabled = isEnabled;
        ClearFilesButton.IsEnabled = isEnabled;
        // ListBox 자체를 끄면 내부 ScrollViewer도 비활성화되므로 스크롤은 유지
        QueueDropBorder.IsEnabled = true;
        QueueListBox.IsEnabled = true;
        QueueListBox.Opacity = isEnabled ? 1 : 0.65;
        DragDrop.SetAllowDrop(QueueDropBorder, isEnabled);
        DragDrop.SetAllowDrop(QueueListBox, isEnabled);
        UseSourceDirectoryCheckBox.IsEnabled = isEnabled;
        OutputDirectoryTextBox.IsEnabled = isEnabled;
        UpdateOutputDirectoryControls();
        EncodingProfileComboBox.IsEnabled = isEnabled;
        UpdateEncodingProfileControls();
        AudioGainNumericUpDown.IsEnabled = isEnabled;
        DynamicAudioNormalizationCheckBox.IsEnabled = isEnabled;
        ResetSettingsButton.IsEnabled = isEnabled;
        UpdateQueueUi();
    }

    private void UpdateOutputDirectoryControls()
    {
        var useSourceDirectory = UseSourceDirectoryCheckBox.IsChecked == true;
        OutputDirectoryTextBox.IsReadOnly = useSourceDirectory;
        PickOutputFolderButton.IsEnabled = _encodingControlsEnabled && !useSourceDirectory;
        OpenOutputFolderButton.IsEnabled = _encodingControlsEnabled && !useSourceDirectory;
    }

    private void UpdateEncodeButton()
    {
        if (_isEncoding)
        {
            EncodeButton.IsEnabled = true;
            EncodeButtonIcon.Icon = FluentIcons.Common.Icon.DismissCircle;
            EncodeButtonText.Text = "중지하기";
            return;
        }

        EncodeButtonIcon.Icon = FluentIcons.Common.Icon.PlayCircle;
        EncodeButtonText.Text = "인코딩 시작";
        EncodeButton.IsEnabled = !_isPreparingEncoders
            && !string.IsNullOrWhiteSpace(_ffmpegExecutable);
    }

    private void ToggleLogVisibility(object? sender, RoutedEventArgs e)
    {
        _isLogVisible = !_isLogVisible;
        LogPanel.IsVisible = _isLogVisible;
        UpdateAuxiliaryPanelVisibility();
        ToggleLogButtonText.Text = _isLogVisible ? "숨김" : "표시";

        if (_isLogVisible)
        {
            FlushLog();
            ScrollLogToEnd();
        }
    }

    private void SetStatus(string message)
    {
        if (_isEncoding)
        {
            EncodingProgressTextBlock.Text = message;
            EncodingProgressTextBlock.IsVisible = true;
            return;
        }

        StatusTextBlock.Text = message;
    }

    private void ReportEncoderPreparation(string encoder, string message)
    {
        SetStatus(message);
        AppendLog($"[{encoder} 준비] {message}");
    }

    private sealed class EncoderPreparationProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string message)
        {
            // Finish each UI update before installation can report overall readiness.
            if (Dispatcher.UIThread.CheckAccess())
            {
                report(message);
            }
            else
            {
                Dispatcher.UIThread.Invoke(() => report(message));
            }
        }
    }

    private void ShowIndeterminateProgress(string? message = null)
    {
        if (_isEncoding)
        {
            EncodingSettingsPanel.IsVisible = false;
        }

        EncodingProgressBar.Value = 0;
        EncodingProgressBar.IsIndeterminate = true;
        EncodingProgressBar.IsVisible = true;
        EncodingProgressTextBlock.Text = message ?? string.Empty;
        EncodingProgressTextBlock.IsVisible = !string.IsNullOrEmpty(message);
        UpdateAuxiliaryPanelVisibility();
    }

    private void HideEncodingProgress()
    {
        EncodingProgressBar.IsVisible = false;
        EncodingProgressTextBlock.IsVisible = false;
        EncodingSettingsPanel.IsVisible = true;
        UpdateAuxiliaryPanelVisibility();
    }

    private void UpdateAuxiliaryPanelVisibility() =>
        AuxiliaryPanel.IsVisible = _isLogVisible || EncodingProgressBar.IsVisible;

    private void UpdateEncodingProgress(EncodingProgress progress, int itemNumber)
    {
        if (progress.IsCompleted)
        {
            ShowIndeterminateProgress($"{itemNumber}/{EncodingQueue.Count} · {progress.Stage ?? "인코딩 마무리 중..."}");
            return;
        }

        if (progress.TotalDuration is not { } totalDuration || totalDuration <= TimeSpan.Zero)
        {
            return;
        }

        var percentage = Math.Clamp(
            progress.ProcessedDuration.TotalSeconds / totalDuration.TotalSeconds * 100,
            0,
            100);
        EncodingProgressBar.IsIndeterminate = false;
        EncodingProgressBar.Value = percentage;
        EncodingProgressTextBlock.IsVisible = true;

        var speedText = progress.Speed is { } speed ? $" · {speed:0.00}x" : string.Empty;
        var etaText = progress.Speed is { } processingSpeed
            ? $" · 예상 남은 시간 {FormatDuration(TimeSpan.FromSeconds(
                Math.Max(0, (totalDuration - progress.ProcessedDuration).TotalSeconds / processingSpeed)))}"
            : " · 예상 남은 시간 계산 중...";
        EncodingProgressTextBlock.Text = $"{itemNumber}/{EncodingQueue.Count} · {percentage:0.0}%{speedText}{etaText}";
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");

    private string FormatEncodingStatus(int itemNumber, string action, string fileName) =>
        $"[{DateTime.Now:HH:mm:ss}] {itemNumber}/{EncodingQueue.Count} {action}: {fileName}";

    private void AppendLog(string message)
    {
        _logBuffer.Report(message);
    }

    private void ClearLog()
    {
        _logBuffer.Clear();
        LogTextBox.Text = string.Empty;
    }

    private void FlushLog()
    {
        if (!_isLogVisible || !_logBuffer.TryGetChangedText(out var text))
        {
            return;
        }

        LogTextBox.Text = text;
        ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        Dispatcher.UIThread.Post(
            () => LogTextBox.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault()?
                .ScrollToEnd(),
            DispatcherPriority.Background);
    }
}
