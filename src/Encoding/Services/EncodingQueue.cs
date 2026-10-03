using System.Collections.ObjectModel;
using Encoder127c.Encoding.Models;

namespace Encoder127c.Encoding.Services;

/// <summary>Owns queue membership and order independently of window controls.</summary>
internal sealed class EncodingQueue
{
    public ObservableCollection<EncodingQueueItem> Items { get; } = [];
    public string? SortColumn { get; private set; }
    public bool SortDescending { get; private set; }

    public (int Added, int Requeued) AddFiles(IEnumerable<string?> paths)
    {
        var existingPaths = new HashSet<string>(
            Items.Select(item => item.Path),
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
                    var existingItem = Items.First(item =>
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

                Items.Add(new EncodingQueueItem(fullPath));
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

        if (added > 0 || requeued > 0) ResetSort();
        return (added, requeued);
    }

    public void Sort(string column)
    {
        SortDescending = SortColumn == column && !SortDescending;
        SortColumn = column;
        IOrderedEnumerable<EncodingQueueItem> sorted = column switch
        {
            "FileSize" => SortDescending ? Items.OrderByDescending(item => item.FileSize) : Items.OrderBy(item => item.FileSize),
            "Status" => SortDescending ? Items.OrderByDescending(item => item.Status) : Items.OrderBy(item => item.Status),
            _ => SortDescending
                ? Items.OrderByDescending(item => item.FileName, StringComparer.CurrentCultureIgnoreCase)
                : Items.OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase)
        };
        var items = sorted.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var oldIndex = Items.IndexOf(items[index]);
            if (oldIndex != index) Items.Move(oldIndex, index);
        }
    }

    public void Move(EncodingQueueItem item, int index)
    {
        var oldIndex = Items.IndexOf(item);
        if (oldIndex >= 0 && oldIndex != index)
        {
            Items.Move(oldIndex, index);
            ResetSort();
        }
    }

    public void ResetSort() => SortColumn = null;

    public void Remove(IEnumerable<EncodingQueueItem> items)
    {
        foreach (var item in items.ToArray()) Items.Remove(item);
    }

    public void Clear()
    {
        Items.Clear();
        ResetSort();
    }
}
