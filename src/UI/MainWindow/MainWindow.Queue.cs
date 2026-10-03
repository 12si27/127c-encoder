using System.ComponentModel;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Encoder127c.Encoding.Models;
using Encoder127c.Settings;
using Encoder127c.Tools;
using static Encoder127c.UI.Dialogs.DialogFactory;

namespace Encoder127c;

public partial class MainWindow
{
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

    private async void OpenQueueItemSettings(object? sender, RoutedEventArgs e)
    {
        var items = GetQueueActionItems(sender).OrderBy(EncodingQueue.IndexOf).ToArray();
        if (IsEncoding || _isPreparingEncoders || items.Length == 0)
        {
            return;
        }

        var firstItem = items[0];
        var dialog = new VideoSettingsDialog(firstItem.Path, _ffmpegExecutable,
            ReadCommonOutputSettings(), ReadCommonGainSettings(),
            GetSelectedTag(EncodingProfileComboBox) ?? DefaultEncodingPreset.DefaultEncodingProfile,
            GetSelectedTag(VideoPresetComboBox) ?? DefaultEncodingPreset.DefaultVideoPreset,
            GetSelectedTag(DeinterlaceModeComboBox) ?? DefaultEncodingPreset.DefaultDeinterlaceMode,
            new VideoBitrateSettings(VideoMaxBitrateNumericUpDown.Value ?? DefaultEncodingPreset.DefaultBitrate.MaxBitrate, VideoBufferSizeNumericUpDown.Value ?? DefaultEncodingPreset.DefaultBitrate.BufferSize),
            firstItem.Settings, _settings.AudioGainOptions,
            items.Length);
        if (await dialog.ShowDialog<VideoSettingsDialogResult?>(this) is { } result)
        {
            foreach (var item in items.Where(EncodingQueue.Contains))
            {
                item.Settings = result.Settings;
            }
        }
    }

    private async void AnalyzeQueueItemGain(object? sender, RoutedEventArgs e)
    {
        var items = GetQueueActionItems(sender);
        if (IsEncoding || _isPreparingEncoders || items.Length != 1)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_ffmpegExecutable))
        {
            await ShowMessageDialogAsync("게인을 측정하려면 FFmpeg를 먼저 다운로드하세요.", DialogKind.Warning);
            return;
        }

        var dialog = new AudioGainDialog(_ffmpegExecutable, items[0].Path, _settings.AudioGainOptions);
        var result = await dialog.ShowDialog<decimal?>(this);
        _settings.AudioGainOptions = dialog.Options;
        if (result is { } gain)
        {
            if (DynamicAudioNormalizationCheckBox.IsChecked == true &&
                await ShowConfirmationDialogAsync(
                    "노멀라이징이 체크되어 있습니다. 해제할까요?",
                    "노멀라이징을 해제하지 않으면 과도한 클리핑이 발생할 수 있습니다."))
            {
                DynamicAudioNormalizationCheckBox.IsChecked = false;
            }

            AudioGainNumericUpDown.Value = gain;
        }
    }

    private async Task PickInputFilesAsync()
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || IsEncoding)
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
        e.DragEffects = !IsEncoding && e.DataTransfer.TryGetFiles()?.Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueDrop(object? sender, DragEventArgs e)
    {
        if (IsEncoding)
        {
            return;
        }

        AddFiles(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()) ?? []);
        e.Handled = true;
    }

    private void AddFiles(IEnumerable<string?> paths)
    {
        var (added, requeued) = _queue.AddFiles(paths);
        if (added > 0 || requeued > 0)
        {
            UpdateQueueSortHeaders();
            SetStatus(requeued > 0
                ? $"{added}개 파일을 추가하고 완료된 {requeued}개 파일을 다시 대기열에 넣었습니다. 총 {EncodingQueue.Count}개"
                : $"{added}개 파일을 추가했습니다. 총 {EncodingQueue.Count}개");
        }
        UpdateQueueUi();
    }

    private void QueueRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Grid row) UpdateQueueColumnWidths(row);
    }

    private void UpdateQueueColumnWidths()
    {
        foreach (var container in QueueListBox.GetRealizedContainers())
        {
            var row = container.GetVisualDescendants().OfType<Grid>()
                .FirstOrDefault(grid => grid.Name == "QueueRowGrid");
            if (row is not null) UpdateQueueColumnWidths(row);
        }
    }

    private void UpdateQueueColumnWidths(Grid row)
    {
        for (var column = 1; column < 4; column++)
        {
            var width = new GridLength(QueueHeader.ColumnDefinitions[column].ActualWidth);
            if (row.ColumnDefinitions[column].Width != width)
                row.ColumnDefinitions[column].Width = width;
        }
    }

    private void SortQueue(object? sender, RoutedEventArgs e)
    {
        if (IsEncoding || !_encodingControlsEnabled || sender is not Button { Tag: string column })
            return;

        EndQueueDrag();
        var selected = GetSelectedQueueItems();
        _queue.Sort(column);
        QueueListBox.SelectedItems?.Clear();
        foreach (var item in selected) QueueListBox.SelectedItems?.Add(item);
        UpdateQueueSortHeaders();
        UpdateQueueUi();
    }

    private void ResetQueueSort()
    {
        _queue.ResetSort();
        UpdateQueueSortHeaders();
    }

    private void UpdateQueueSortHeaders()
    {
        foreach (var (header, column, text) in new[]
                 { (FileQueueHeader, "FileName", "파일"), (SizeQueueHeader, "FileSize", "용량"),
                     (StatusQueueHeader, "Status", "상태") })
            header.Content = text + (_queue.SortColumn == column ? _queue.SortDescending ? " ▼" : " ▲" : "");
    }

    private void QueueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
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

    private void InvertQueueSelection(object? sender, RoutedEventArgs e)
    {
        var selected = GetSelectedQueueItems().ToHashSet();
        QueueListBox.SelectedItems?.Clear();
        foreach (var item in EncodingQueue.Where(item => !selected.Contains(item)))
            QueueListBox.SelectedItems?.Add(item);
        QueueListBox.Focus();
    }

    private void QueueMenuOpening(object? sender, EventArgs e)
    {
        var selected = GetSelectedQueueItems();
        SelectAllQueueMenuItem.IsEnabled = EncodingQueue.Count > 0;
        SelectAllQueueMenuItem.InputGesture = new KeyGesture(Key.A,
            OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        InvertQueueSelectionMenuItem.IsEnabled = EncodingQueue.Count > 0;
        ResetSelectedSettingsMenuItem.IsVisible = selected.Any(item => item.HasCustomSettings);
        ResetSelectedSettingsMenuItem.IsEnabled = !IsEncoding && !_isPreparingEncoders;
        ResetSelectedStatusMenuItem.IsEnabled = selected.Length > 0 && !IsEncoding;
    }

    private void OpenProgramDirectory(object? sender, RoutedEventArgs e) =>
        OpenDirectory(AppContext.BaseDirectory);

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
        if (IsEncoding || !_encodingControlsEnabled)
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
        if (_queueDragItem is not { } item || IsEncoding || !_encodingControlsEnabled)
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
            var insertionIndex = QueueListBox.IndexFromContainer(target) + (insertAfter ? 1 : 0);
            var oldIndex = EncodingQueue.IndexOf(item);
            if (insertionIndex == oldIndex || insertionIndex == oldIndex + 1)
            {
                return;
            }

            _queueInsertionIndex = insertionIndex;
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
            if (item is not null && insertionIndex >= 0 && !IsEncoding && _encodingControlsEnabled)
            {
                var oldIndex = EncodingQueue.IndexOf(item);
                var newIndex = insertionIndex > oldIndex ? insertionIndex - 1 : insertionIndex;
                if (oldIndex >= 0 && newIndex != oldIndex && newIndex < EncodingQueue.Count)
                {
                    _queue.Move(item, newIndex);
                    ResetQueueSort();
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
        var actionItems = GetQueueActionItems(menu);
        menuItems[1].IsEnabled = actionItems
            .Any(selected => !string.IsNullOrWhiteSpace(GetQueueItemOutputDirectory(selected)));
        menuItems[2].IsEnabled = !IsEncoding && !_isPreparingEncoders;
        menuItems[3].IsEnabled = !IsEncoding && !_isPreparingEncoders && _ffmpegExecutable is not null
            && actionItems.Length == 1;
        menuItems[4].IsVisible = actionItems.Any(selected => selected.HasCustomSettings);
        menuItems[4].IsEnabled = !IsEncoding && !_isPreparingEncoders;
        menuItems[5].IsEnabled = !IsEncoding;
        menuItems[6].IsEnabled = !IsEncoding;
        menuItems[7].InputGesture = new KeyGesture(Key.A,
            OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
    }

    private void OpenQueueItemSourceFolder(object? sender, RoutedEventArgs e)
    {
        OpenQueueDirectories(GetQueueActionItems(sender).Select(item => item.SourceDirectory));
    }

    private async void OpenQueueItemOutputFolder(object? sender, RoutedEventArgs e)
    {
        var items = GetQueueActionItems(sender);
        var folders = new List<string?>();
        foreach (var item in items)
        {
            if (item.Status == EncodingQueueStatus.Completed && item.OutputPath is { } outputPath
                && File.Exists(outputPath) && await TryRevealFileAsync(outputPath))
            {
                continue;
            }

            folders.Add(GetQueueItemOutputDirectory(item));
        }

        OpenQueueDirectories(folders, createIfMissing: true);
    }

    private static async Task<bool> TryRevealFileAsync(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe"
                    : OperatingSystem.IsMacOS() ? "open" : "gdbus",
                UseShellExecute = false
            };
            if (OperatingSystem.IsWindows())
            {
                startInfo.Arguments = $"/select,\"{fullPath}\"";
            }
            else if (OperatingSystem.IsMacOS())
            {
                startInfo.ArgumentList.Add("-R");
                startInfo.ArgumentList.Add(fullPath);
            }
            else
            {
                // FileManager1 selects the URI in the user's default file manager.
                foreach (var argument in new[]
                {
                    "call", "--session", "--timeout", "5",
                    "--dest", "org.freedesktop.FileManager1",
                    "--object-path", "/org/freedesktop/FileManager1",
                    "--method", "org.freedesktop.FileManager1.ShowItems",
                    $"['{new Uri(fullPath).AbsoluteUri.Replace("'", "%27")}']", ""
                })
                {
                    startInfo.ArgumentList.Add(argument);
                }
            }

            using var process = Process.Start(startInfo);
            if (process is null) return false;
            // Explorer may hand the request to an existing process and exit with a nonzero code.
            if (OperatingSystem.IsWindows()) return true;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
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
        item.OutputDirectory ?? GetOutputDirectory(item).Trim();

    private void ResetQueueItemStatus(object? sender, RoutedEventArgs e) =>
        ResetQueueStatus(GetQueueActionItems(sender));

    private void ResetSelectedQueueStatus(object? sender, RoutedEventArgs e) =>
        ResetQueueStatus(GetSelectedQueueItems());

    private void ResetQueueStatus(EncodingQueueItem[] items)
    {
        if (IsEncoding)
        {
            return;
        }

        foreach (var item in items)
        {
            item.ResetStatus();
        }

        SetStatus($"{items.Length}개 파일의 상태를 초기화했습니다.");
    }

    private void ResetQueueItemSettings(object? sender, RoutedEventArgs e) =>
        ResetQueueSettings(GetQueueActionItems(sender));

    private void ResetSelectedQueueSettings(object? sender, RoutedEventArgs e) =>
        ResetQueueSettings(GetSelectedQueueItems());

    private void ResetQueueSettings(EncodingQueueItem[] selected)
    {
        if (IsEncoding || _isPreparingEncoders)
        {
            return;
        }

        var items = selected.Where(item => item.HasCustomSettings).ToArray();
        foreach (var item in items)
        {
            item.Settings = null;
        }

        if (items.Length > 0)
        {
            SetStatus($"{items.Length}개 파일의 비디오 설정을 초기화했습니다.");
        }
    }

    private void RemoveQueueItem(object? sender, RoutedEventArgs e)
    {
        if (IsEncoding)
        {
            return;
        }

        RemoveQueueItems(GetQueueActionItems(sender));
    }

    private void RemoveQueueItems(EncodingQueueItem[] items)
    {
        _queue.Remove(items);

        UpdateQueueUi();
    }

    private void RemoveSelectedFile(object? sender, RoutedEventArgs e)
    {
        if (IsEncoding)
        {
            return;
        }

        RemoveQueueItems(GetSelectedQueueItems());
    }

    private void ClearFiles(object? sender, RoutedEventArgs e)
    {
        if (IsEncoding)
        {
            return;
        }

        _queue.Clear();
        ResetQueueSort();
        SetStatus("파일 목록을 비웠습니다.");
        UpdateQueueUi();
    }

}
