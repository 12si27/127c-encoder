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

    public EncodingQueueItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        FileSizeText = FormatFileSize(new FileInfo(path).Length);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string FileName { get; }
    public string FileSizeText { get; }

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
