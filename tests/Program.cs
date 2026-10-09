using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Encoder127c;
using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Arguments;
using Encoder127c.Encoding.Services;
using Encoder127c.Encoding.Validation;
using Encoder127c.Encoders.Ffmpeg.Services;
using Encoder127c.Encoders.Fdkaac.Services;
using Encoder127c.Settings;
using Encoder127c.Platform;
using Encoder127c.Tools;
using Encoder127c.UI.Dialogs;

var scratch = Path.Combine(Path.GetTempPath(), $"127c-regression-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
try
{
    var input = Path.Combine(scratch, "a.mp4");
    var second = Path.Combine(scratch, "b.mp4");
    File.WriteAllText(input, "sample");
    File.WriteAllText(second, "longer sample");
    CheckQueue(input, second);
    CheckSettings();
    CheckColorNormalization(input, scratch);
    CheckLinuxDesktopIntegration();
    await CheckSession(input, scratch, "success");
    await CheckSession(input, scratch, "cancel");
    await CheckSession(input, scratch, "partial");
    await CheckSession(input, scratch, "failure");
    await CheckPreparationFailure(input, scratch);
    CheckWindows(input);
    CheckWindowClose(input, scratch, null);
    CheckWindowClose(input, scratch, true);
    CheckWindowClose(input, scratch, false);
    Console.WriteLine("PASS: queue, common settings, Linux desktop registration classification, session success/cancel/partial/failure, cleanup completion, XAML and icons, window close with immediate stop/partial save/discard");
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void CheckQueue(string input, string second)
{
    var queue = new EncodingQueue();
    Require(queue.AddFiles([input, second, input, null, "", input + ".missing"]) == (2, 0), "Queue must ignore duplicates and unavailable files.");
    var item = queue.Items[0];
    item.BeginEncoding("old-output.mp4");
    item.Status = EncodingQueueStatus.Completed;
    Require(queue.AddFiles([input]) == (0, 1) && item.Status == EncodingQueueStatus.Pending && item.OutputPath is null,
        "Readding a completed item must reset output and status without duplicating it.");
    queue.Sort("FileSize");
    Require(queue.Items[0] == item && !queue.SortDescending, "First size sort must ascend.");
    queue.Sort("FileSize");
    Require(queue.Items[1] == item && queue.SortDescending, "Second size sort must descend and preserve item identity.");
    queue.Move(item, 0);
    Require(queue.Items[0] == item && queue.SortColumn is null, "Manual reorder must preserve identity and clear sorting.");
    queue.Remove([item]);
    Require(queue.Items.Count == 1, "Remove must update the bound collection.");
    queue.Clear();
    Require(queue.Items.Count == 0 && queue.SortColumn is null, "Clear must reset ordering.");
}

static void CheckSettings()
{
    var state = new CommonSettingsState();
    state.Restore(new EncoderSettings { EncodingProfile = DefaultEncodingPreset.EncodingProfileSaving, VideoMaxBitrate = 999 });
    Require(state.DefaultMaxBitrate == DefaultEncodingPreset.DefaultBitrate.MaxBitrate, "Saving profile values must not replace default profile bitrates.");
    state.Restore(new EncoderSettings { VideoMaxBitrate = 3456, VideoBufferSize = 7890 });
    Require(state.DefaultMaxBitrate == 3456 && state.DefaultBufferSize == 7890, "Legacy common values must restore for the default profile.");
    state.CaptureBitrates(-1, 2_000_000);
    Require(state.DefaultMaxBitrate == DefaultEncodingPreset.DefaultBitrate.MaxBitrate, "Invalid bitrate must fall back to default.");
    state.Restore(new EncoderSettings { AudioGainAnalysis = new AudioGainOptions(IgnoreTopPercent: 100) });
    Require(state.AudioGainOptions.IgnoreTopPercent == 5, "Invalid saved analysis conditions must retain defaults.");
    state.Reset();
    Require(state.DefaultBufferSize == DefaultEncodingPreset.DefaultBitrate.BufferSize, "Reset must restore defaults.");
}

static void CheckColorNormalization(string input, string output)
{
    static string ValueAfter(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        Require(index >= 0 && index < args.Length - 1, $"Missing FFmpeg option: {option}");
        return args[index + 1];
    }

    const string hdr10 = "Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084, progressive)";
    const string bt601 = "Stream #0:0: Video: mpeg2video, yuv420p(tv, smpte170m/smpte170m/smpte170m)";
    Require(VideoPreviewReader.DetectColorConversion(hdr10) == VideoColorConversion.HdrToBt709,
        "HDR10 must be tone-mapped rather than merely retagged.");
    Require(VideoPreviewReader.DetectColorConversion(hdr10.Replace("smpte2084", "arib-std-b67")) == VideoColorConversion.HdrToBt709,
        "HLG must use the HDR tone-mapping pipeline.");
    Require(VideoPreviewReader.DetectColorConversion(bt601) == VideoColorConversion.SdrToBt709,
        "Tagged BT.601 input must be color-converted.");
    Require(VideoPreviewReader.DetectColorConversion(
        "Stream #0:0: Video: h264, yuv420p(tv, bt709/bt709/bt709)") == VideoColorConversion.None,
        "BT.709 source must not incur extra color conversion.");
    Require(VideoPreviewReader.DetectColorConversion(
        "Stream #0:0: Video: h264, yuv420p(pc, bt709)") == VideoColorConversion.FullRangeToLimited,
        "Full-range source must be converted to limited range.");
    try
    {
        VideoPreviewReader.DetectColorConversion(
            "Stream #0:0: Video: hevc, yuv420p10le(tv, bt2020nc/unknown/unknown)");
        throw new Exception("Incomplete BT.2020 input metadata must not be silently relabeled.");
    }
    catch (InvalidOperationException) { }

    var raw = Request(new EncodingQueueItem(input), output);
    var request = new VideoEncodingRequestValidator().Validate(raw).Request!;
    var builder = new FfmpegArgumentBuilder();
    var standard = builder.BuildVideoOnly(request, request.OutputPath).ToArray();
    foreach (var (option, expected) in new[]
    {
        ("-pix_fmt", "yuv420p"),
        ("-color_primaries", "bt709"),
        ("-color_trc", "bt709"),
        ("-colorspace", "bt709"),
        ("-color_range", "tv")
    })
    {
        Require(ValueAfter(standard, option) == expected, $"{option} must be fixed to SDR BT.709.");
    }

    var hdr = builder.BuildVideoOnly(request with { ColorConversion = VideoColorConversion.HdrToBt709 },
        request.OutputPath).ToArray();
    var hdrFilter = ValueAfter(hdr, "-vf");
    Require(hdrFilter.Contains("tonemap=tonemap=hable") && hdrFilter.Contains("zscale=p=bt709") &&
        hdrFilter.Contains("format=yuv420p"), "HDR conversion must include actual tone mapping and 8-bit output.");

    var sdr = builder.BuildVideoOnly(request with { ColorConversion = VideoColorConversion.SdrToBt709 },
        request.OutputPath).ToArray();
    Require(ValueAfter(sdr, "-vf").Contains("zscale=p=bt709") &&
        !ValueAfter(sdr, "-vf").Contains("tonemap="), "SDR color conversion must not tone-map.");

    var saving = builder.BuildVideoOnly(request with
    {
        EncodingProfile = DefaultEncodingPreset.EncodingProfileSaving,
        ColorConversion = VideoColorConversion.HdrToBt709
    }, request.OutputPath).ToArray();
    Require(ValueAfter(saving, "-vf").Contains("scale=-2:min(720\\,ih)"),
        "Saving profile must retain its downscale after tone mapping.");
}

static void CheckLinuxDesktopIntegration()
{
    static void Expect(LinuxDesktopIntegration.ChangeKind expected, string? desktop, string? icon,
        string? previousDesktop = "old-desktop", string? previousIcon = "old-icon",
        string? previousPath = "/opt/127c-encoder", string path = "/opt/127c-encoder")
    {
        var actual = LinuxDesktopIntegration.Classify(desktop, icon, "new-desktop", "new-icon",
            previousDesktop, previousIcon, previousPath, path);
        Require(actual == expected, $"Desktop integration expected {expected}, got {actual}.");
    }

    Expect(LinuxDesktopIntegration.ChangeKind.UpToDate, "new-desktop", "new-icon");
    Expect(LinuxDesktopIntegration.ChangeKind.Missing, null, "new-icon");
    Expect(LinuxDesktopIntegration.ChangeKind.Missing, "new-desktop", null);
    Expect(LinuxDesktopIntegration.ChangeKind.Changed, "old-desktop", "old-icon");
    Expect(LinuxDesktopIntegration.ChangeKind.LocationChanged, "old-desktop", "old-icon",
        path: "/home/user/Downloads/127c-encoder");
    Expect(LinuxDesktopIntegration.ChangeKind.Customized, "user-desktop", "old-icon");
    Expect(LinuxDesktopIntegration.ChangeKind.Customized, "old-desktop", "user-icon");
    Expect(LinuxDesktopIntegration.ChangeKind.Customized, "unknown-desktop", "new-icon",
        previousDesktop: null);
}

static VideoEncodingRequest Request(EncodingQueueItem item, string output) =>
    VideoEncodingRequestFactory.Create(item.Path, new VideoSettings(Output: new VideoOutputSettings(output, false)), item.Settings);

static async Task CheckSession(string input, string output, string mode)
{
    var encoder = new ControlledEncoder();
    var session = new EncodingSession(new VideoEncodingRequestValidator(), encoder);
    var items = new List<EncodingQueueItem> { new(input), new(input) };
    var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var prepared = false;
    var run = session.RunAsync("fake", items, items, item => Request(item, output), new SilentLog(),
        () => { prepared = session.IsRunning; return Task.CompletedTask; },
        async _ => { Require(!session.IsRunning, "Cleanup must see stopped state."); cleanupEntered.SetResult(); await cleanupRelease.Task; });
    await encoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(prepared && session.IsRunning && session.CurrentRun is not null, "Session must become active before preparation and encoding.");
    var completion = session.Completion;
    try
    {
        await session.RunAsync("fake", items, items, item => Request(item, output), new SilentLog(),
            () => Task.CompletedTask, _ => Task.CompletedTask);
        throw new Exception("Concurrent sessions must be rejected.");
    }
    catch (InvalidOperationException) { }
    if (mode == "cancel") session.Stop();
    else if (mode == "partial") session.Stop(savePartial: true);
    else encoder.Result.TrySetResult(mode == "failure" ? 1 : 0);
    await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(!completion.IsCompleted, "Completion must wait for UI cleanup, so close cannot race cleanup.");
    cleanupRelease.SetResult();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
    await completion.WaitAsync(TimeSpan.FromSeconds(5));
    var expected = mode switch
    {
        "cancel" or "partial" => EncodingQueueStatus.Stopped,
        "failure" => EncodingQueueStatus.Failed,
        _ => EncodingQueueStatus.Completed
    };
    Require(items[0].Status == expected, $"Unexpected {mode} item state.");
    Require(encoder.Calls == (mode is "cancel" or "partial" ? 1 : 2), "Stopped batch must not start the next file; normal and failed batches must continue.");
    Require(!session.IsRunning && session.CurrentRun is null, "Session must release active state.");
    encoder.ResetResult();
    encoder.Result.TrySetResult(0);
    await session.RunAsync("fake", items, [items[0]], item => Request(item, output), new SilentLog(),
        () => Task.CompletedTask, _ => Task.CompletedTask);
    Require(items[0].Status == EncodingQueueStatus.Completed && session.Completion.IsCompleted,
        "A stopped or failed session must be reusable.");
}

static async Task CheckPreparationFailure(string input, string output)
{
    var session = new EncodingSession(new VideoEncodingRequestValidator(), new ControlledEncoder());
    var cleaned = false;
    var items = new List<EncodingQueueItem> { new(input) };
    try
    {
        await session.RunAsync("fake", items, items, item => Request(item, output), new SilentLog(),
            () => throw new IOException("prepare failed"), _ => { cleaned = true; return Task.CompletedTask; });
        throw new Exception("Preparation error must propagate.");
    }
    catch (IOException) { }
    Require(cleaned && !session.IsRunning && session.Completion.IsCompleted, "Preparation failure must still clean up and release completion.");
}

static void CheckWindows(string input)
{
    AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
    var services = VideoEncodingServices.CreateDefault();
    var window = new MainWindow(new NoDownloadFfmpeg(), services);
    Require(window.Icon is not null, "Moved icon resource must load.");
    Require(AssetLoader.Exists(new Uri("avares://127c-encoder/src/Assets/app-icon-mac.png")), "Moved macOS icon resource must exist.");
    var addFiles = typeof(MainWindow).GetMethod("AddFiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
    addFiles.Invoke(window, [new string?[] { input }]);
    Require(window.EncodingQueue.Count == 1 && window.FindControl<ListBox>("QueueListBox") is not null, "Queue binding and named XAML controls must survive extraction.");
    Window[] dialogs = [new LogWindow(), new VideoTrimDialog(), new AudioGainDialog(), new VideoSettingsDialog(),
        DialogFactory.CreateDialog("test", "test", [("OK", true)])];
    foreach (var dialog in dialogs) Require(dialog.Content is not null, "Moved dialog XAML must load.");
}

static void CheckWindowClose(string input, string output, bool? savePartial)
{
    // The portable settings file belongs to this test executable's output directory.
    var settingsPath = Path.Combine(AppPaths.DataDirectory, "settings.json");
    var previous = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
    var encoder = new ControlledEncoder();
    var window = new MainWindow(new NoDownloadFfmpeg(),
        new VideoEncodingServices(new VideoEncodingRequestValidator(), encoder, new NoDownloadFdkaac()));
    var closed = false;
    window.Closed += (_, _) => closed = true;
    try
    {
        window.FindControl<TextBox>("OutputDirectoryTextBox")!.Text = output;
        window.FindControl<CheckBox>("UseSourceDirectoryCheckBox")!.IsChecked = false;
        var sleepItem = (MenuItem)typeof(MainWindow).GetField("PreventSleepMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        sleepItem.IsChecked = false;
        window.EncodingQueue.Add(new EncodingQueueItem(input));
        window.Show();
        typeof(MainWindow).GetMethod("StartEncoding", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [null, new RoutedEventArgs()]);
        var session = (EncodingSession)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        PumpUntil(() => encoder.Started.Task.IsCompleted);
        Require(session.IsRunning && !window.FindControl<Control>("EncodingProfilePanel")!.IsVisible,
            "Starting through the window must activate the session and hide settings.");
        if (savePartial is not null)
            typeof(MainWindow).GetField("_hasEncodingProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
        Require(!closed || session.Completion.IsCompleted, "Window must wait for active encoding cleanup before closing.");
        if (savePartial is not null)
        {
            var dialog = window.OwnedWindows.Single();
            var button = dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, savePartial == true ? "네" : "아니오"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        PumpUntil(() => closed);
        Require(session.Completion.IsCompleted && window.EncodingQueue[0].Status == EncodingQueueStatus.Stopped,
            "Closing must finish the canonical stop flow before disposing the window.");
    }
    finally
    {
        if (previous is null) File.Delete(settingsPath);
        else File.WriteAllBytes(settingsPath, previous);
    }
}

static void PumpUntil(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Headless UI operation timed out.");
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
    }
}

sealed class SilentLog : IProgress<string>
{
    public void Report(string value) { }
}

sealed class ControlledEncoder : IVideoEncoder
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<int> Result { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get; private set; }
    public void ResetResult() => Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<VideoEncodingResult> EncodeAsync(string executable, ValidatedVideoEncodingRequest request,
        IProgress<string>? logProgress = null, IProgress<EncodingProgress>? encodingProgress = null,
        CancellationToken cancellationToken = default, CancellationToken finishEarlyToken = default)
    {
        Calls++;
        Started.TrySetResult();
        using var partial = finishEarlyToken.Register(() => Result.TrySetResult(0));
        var code = await Result.Task.WaitAsync(cancellationToken);
        return new VideoEncodingResult(code, "");
    }
}

sealed class NoDownloadFfmpeg : IFfmpegManager
{
    public Task<string?> FindAvailableExecutableAsync(CancellationToken token = default) => Task.FromResult<string?>("fake");
    public Task<string> EnsureAvailableAsync(IProgress<string>? progress = null, CancellationToken token = default) =>
        throw new InvalidOperationException("Headless tests must not download encoders.");
}

sealed class NoDownloadFdkaac : IFdkaacManager
{
    public Task<string?> FindAvailableExecutableAsync(CancellationToken token = default) => Task.FromResult<string?>("fake");
    public Task<string> EnsureAvailableAsync(CancellationToken token = default, IProgress<string>? progress = null) =>
        throw new InvalidOperationException("Headless tests must not download encoders.");
}
