using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace Encoder127c.Encoding.Models;

public enum EncodingQueueStatus
{
    Pending,
    Encoding,
    Completed,
    Failed,
    Stopped
}

public sealed class EncodingQueueItem : INotifyPropertyChanged
{
    private EncodingQueueStatus _status;
    private VideoSettings? _settings;

    public EncodingQueueItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        FileSize = new FileInfo(path).Length;
        FileSizeText = FormatFileSize(FileSize);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string FileName { get; }
    public bool HasCustomSettings => Settings is not null;
    public VideoSettings? Settings
    {
        get => _settings;
        set
        {
            var settings = value is { HasOverrides: true } ? value : null;
            if (_settings == settings) return;
            _settings = settings;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCustomSettings));
        }
    }
    public long FileSize { get; }
    public string FileSizeText { get; }
    public string SourceDirectory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string? OutputDirectory { get; private set; }
    public string? OutputPath { get; private set; }

    public void BeginEncoding(string outputPath)
    {
        OutputPath = outputPath;
        OutputDirectory = System.IO.Path.GetDirectoryName(outputPath);
        Status = EncodingQueueStatus.Encoding;
    }

    public void ResetStatus()
    {
        if (Status == EncodingQueueStatus.Encoding)
        {
            return;
        }

        OutputDirectory = null;
        OutputPath = null;
        Status = EncodingQueueStatus.Pending;
    }

    public EncodingQueueStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Background));
        }
    }

    public string StatusText => Status switch
    {
        EncodingQueueStatus.Pending => "대기",
        EncodingQueueStatus.Encoding => "인코딩 중",
        EncodingQueueStatus.Completed => "완료",
        EncodingQueueStatus.Failed => "오류",
        EncodingQueueStatus.Stopped => "중지됨",
        _ => string.Empty
    };

    public IBrush Background => Status switch
    {
        EncodingQueueStatus.Encoding => new SolidColorBrush(Color.Parse("#304A90E2")),
        EncodingQueueStatus.Completed => new SolidColorBrush(Color.Parse("#204CAF50")),
        EncodingQueueStatus.Failed or EncodingQueueStatus.Stopped => new SolidColorBrush(Color.Parse("#20F44336")),
        _ => Brushes.Transparent
    };

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.0} {units[unitIndex]}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
