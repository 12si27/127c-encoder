using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Validation;

namespace Encoder127c.Encoding.Services;

/// <summary>Owns the active batch and completion, including the caller's UI cleanup.</summary>
internal sealed class EncodingSession(
    IVideoEncodingRequestValidator validator,
    IVideoEncoder encoder)
{
    private readonly EncodingQueueRunner _runner = new(validator, encoder);
    private TaskCompletionSource? _completion;

    public bool IsRunning { get; private set; }
    public bool IsStopping => _runner.IsStopping;
    public object? CurrentRun => _runner.CurrentRun;
    public Task Completion => _completion?.Task ?? Task.CompletedTask;

    public event Action<string>? StatusChanged
    {
        add => _runner.StatusChanged += value;
        remove => _runner.StatusChanged -= value;
    }
    public event Action<EncodingQueueItem, int, string>? ItemStarted
    {
        add => _runner.ItemStarted += value;
        remove => _runner.ItemStarted -= value;
    }
    public event Action<EncodingProgress, int>? ProgressChanged
    {
        add => _runner.ProgressChanged += value;
        remove => _runner.ProgressChanged -= value;
    }

    public void Stop(bool savePartial = false) => _runner.Stop(savePartial);

    public async Task RunAsync(
        string executable,
        List<EncodingQueueItem> queue,
        List<EncodingQueueItem> files,
        Func<EncodingQueueItem, VideoEncodingRequest> createRequest,
        IProgress<string> log,
        Func<Task> prepareBatch,
        Func<string?, Task> finishBatch)
    {
        if (IsRunning) throw new InvalidOperationException("An encoding session is already running.");
        IsRunning = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        string? finalStatus = null;
        try
        {
            finalStatus = await _runner.RunAsync(executable, queue, files, createRequest, log, prepareBatch);
        }
        finally
        {
            IsRunning = false;
            try
            {
                await finishBatch(finalStatus);
            }
            finally
            {
                completion.TrySetResult();
            }
        }
    }
}
