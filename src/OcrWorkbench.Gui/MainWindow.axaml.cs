using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OcrWorkbench.Contracts;
using OcrWorkbench.Core;
using OcrWorkbench.Infrastructure;
using OcrWorkbench.Platform.MacOS;
using OcrWorkbench.PluginHost;

namespace OcrWorkbench.Gui;

public sealed partial class MainWindow : Window
{
    private readonly TextBox _pluginDirectory;
    private readonly TextBox _imagePaths;
    private readonly TextBox _outputPath;
    private readonly Button _runButton;
    private readonly Button _screenshotButton;
    private readonly Button _pauseResumeButton;
    private readonly Button _cancelButton;
    private readonly Button _copyButton;
    private readonly ComboBox _pausedJobs;
    private readonly Button _refreshJobsButton;
    private readonly Button _resumeSelectedButton;
    private readonly Button _cancelSelectedButton;
    private readonly TextBlock _statusText;
    private readonly TextBlock _hotkeyText;
    private readonly TextBox _resultText;
    private readonly GuiSettingsStore _settings = new(GetDefaultSettingsPath());
    private readonly IInteractiveScreenshotService? _screenshotService;
    private readonly IScreenCapturePermissionService? _screenCapturePermissionService;
    private readonly IGlobalHotkeyService? _hotkeyService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _activeOperation;
    private WorkerRecognizerSession? _recognizerSession;
    private string? _recognizerDirectory;
    private JobExecutionService? _activeExecutionService;
    private Guid? _activeJobId;
    private bool _closing;
    private bool _shutdownCompleted;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _pluginDirectory = this.FindControl<TextBox>("PluginDirectory")!;
        _imagePaths = this.FindControl<TextBox>("ImagePaths")!;
        _outputPath = this.FindControl<TextBox>("OutputPath")!;
        _runButton = this.FindControl<Button>("RunButton")!;
        _screenshotButton = this.FindControl<Button>("ScreenshotButton")!;
        _pauseResumeButton = this.FindControl<Button>("PauseResumeButton")!;
        _cancelButton = this.FindControl<Button>("CancelButton")!;
        _copyButton = this.FindControl<Button>("CopyButton")!;
        _pausedJobs = this.FindControl<ComboBox>("PausedJobs")!;
        _refreshJobsButton = this.FindControl<Button>("RefreshJobsButton")!;
        _resumeSelectedButton = this.FindControl<Button>("ResumeSelectedButton")!;
        _cancelSelectedButton = this.FindControl<Button>("CancelSelectedButton")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _hotkeyText = this.FindControl<TextBlock>("HotkeyText")!;
        _resultText = this.FindControl<TextBox>("ResultText")!;
        _runButton.Click += RunClickedAsync;
        _screenshotButton.Click += ScreenshotClickedAsync;
        _pauseResumeButton.Click += PauseResumeClickedAsync;
        _cancelButton.Click += CancelClickedAsync;
        _copyButton.Click += CopyClickedAsync;
        _pausedJobs.SelectionChanged += PausedJobSelectionChanged;
        _refreshJobsButton.Click += RefreshJobsClickedAsync;
        _resumeSelectedButton.Click += ResumeSelectedClickedAsync;
        _cancelSelectedButton.Click += CancelSelectedClickedAsync;
        Closing += WindowClosingAsync;
        Opened += WindowOpenedAsync;

        if (OperatingSystem.IsMacOSVersionAtLeast(15, 2))
        {
            var screenshotService = new MacOSInteractiveScreenshotService();
            _screenshotService = screenshotService;
            _screenCapturePermissionService = screenshotService;
            _hotkeyService = new MacOSGlobalHotkeyService();
            _hotkeyService.Pressed += HotkeyPressed;
            _hotkeyText.Text = "Screenshot shortcut: Control+Option+O";
        }
        else
        {
            _screenshotButton.IsEnabled = false;
        }
    }

    private async void RunClickedAsync(object? sender, RoutedEventArgs eventArgs)
    {
        var images = (_imagePaths.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(_pluginDirectory.Text)
            || string.IsNullOrWhiteSpace(_outputPath.Text)
            || images.Length == 0)
        {
            _statusText.Text = "Plugin, output and at least one image are required.";
            return;
        }

        var operation = BeginOperation();
        if (operation is null)
        {
            _statusText.Text = "Another OCR operation is already active.";
            return;
        }

        _statusText.Text = "Running…";
        _resultText.Text = string.Empty;
        try
        {
            var store = new SqliteJobStore(GetDefaultDatabasePath());
            await store.InitializeAsync(operation.Token);
            var package = await PluginPackage.LoadAsync(_pluginDirectory.Text, operation.Token);
            await _settings.SaveAsync(new GuiSettings(_pluginDirectory.Text), operation.Token);
            var recognizer = await GetRecognizerSessionAsync(package);
            var job = new JobSpec(
                images.Select(path => new ImageInput(path)).ToArray(),
                new RecognitionOptions(),
                new PlainTextExportOptions(_outputPath.Text));
            var executionService = new JobExecutionService(store, recognizer);
            var record = await executionService.SubmitAsync(job, operation.Token);
            _activeExecutionService = executionService;
            _activeJobId = record.Id;
            _pauseResumeButton.Content = "Pause";
            _pauseResumeButton.IsEnabled = true;
            var execution = await executionService.RunAsync(record.Id, operation.Token);
            await PresentBatchResultAsync(execution);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _statusText.Text = "Cancelled.";
            _resultText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            _statusText.Text = "Failed";
            _resultText.Text = exception.Message;
        }
        finally
        {
            _activeExecutionService = null;
            _activeJobId = null;
            EndOperation(operation);
        }
    }

    private async void WindowOpenedAsync(object? sender, EventArgs eventArgs)
    {
        try
        {
            var settings = await _settings.LoadAsync();
            if (!string.IsNullOrWhiteSpace(settings.PluginDirectory))
            {
                _pluginDirectory.Text = settings.PluginDirectory;
            }

            var store = new SqliteJobStore(GetDefaultDatabasePath());
            await store.InitializeAsync();
            var recovered = await new JobRecoveryService(store).ReconcileAbandonedAsync();
            await RefreshPausedJobsAsync(store);
            if (recovered.Count > 0)
            {
                _statusText.Text = $"Recovered {recovered.Count} interrupted task(s).";
            }

            if (_hotkeyService is not null)
            {
                await _hotkeyService.RegisterAsync(HotkeyGesture.DefaultScreenshot);
            }
        }
        catch (Exception exception)
        {
            _statusText.Text = $"Startup failed: {exception.Message}";
        }
    }

    private async void WindowClosingAsync(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _activeOperation?.Cancel();
        if (_hotkeyService is not null)
        {
            _hotkeyService.Pressed -= HotkeyPressed;
        }

        await _operationGate.WaitAsync();
        try
        {
            if (_recognizerSession is not null)
            {
                await _recognizerSession.DisposeAsync();
                _recognizerSession = null;
            }

            if (_hotkeyService is not null)
            {
                await _hotkeyService.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            _statusText.Text = $"Shutdown cleanup failed: {exception.Message}";
        }
        finally
        {
            _operationGate.Release();
            _shutdownCompleted = true;
            Close();
        }
    }

    private void HotkeyPressed(object? sender, EventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() => _ = RunScreenshotAsync());

    private async void ScreenshotClickedAsync(object? sender, RoutedEventArgs eventArgs) =>
        await RunScreenshotAsync();

    private async Task RunScreenshotAsync()
    {
        if (_screenshotService is null)
        {
            _statusText.Text = "Screenshot OCR is unavailable on this platform.";
            return;
        }

        var operation = BeginOperation();
        if (operation is null)
        {
            _statusText.Text = "Another OCR operation is already active.";
            return;
        }

        try
        {
            _statusText.Text = "Checking Screen Recording permission…";
            var permission = await _screenCapturePermissionService!
                .RequestAccessAsync(operation.Token);
            if (permission != ScreenCapturePermissionStatus.Granted)
            {
                Show();
                Activate();
                _statusText.Text = permission == ScreenCapturePermissionStatus.Unsupported
                    ? "Screenshot OCR is unavailable on this platform."
                    : "Screen Recording permission is required; grant it in System Settings and relaunch NEOCR.";
                _resultText.Text = permission == ScreenCapturePermissionStatus.Denied
                    ? "macOS may not display the prompt again after a denial. Open Privacy & Security > Screen Recording, enable NEOCR, and relaunch the app."
                    : string.Empty;
                return;
            }

            if (string.IsNullOrWhiteSpace(_pluginDirectory.Text))
            {
                Show();
                Activate();
                _statusText.Text = "Choose a recognizer plugin before using Screenshot OCR.";
                return;
            }

            _screenshotButton.IsEnabled = false;
            _statusText.Text = "Preparing Screenshot OCR…";
            _resultText.Text = string.Empty;
            _copyButton.IsEnabled = false;

            var store = new SqliteJobStore(GetDefaultDatabasePath());
            await store.InitializeAsync(operation.Token);
            var package = await PluginPackage.LoadAsync(_pluginDirectory.Text, operation.Token);
            await _settings.SaveAsync(new GuiSettings(_pluginDirectory.Text), operation.Token);
            var recognizer = await GetRecognizerSessionAsync(package);

            Hide();
            var result = await new ScreenshotOcrWorkflow(_screenshotService, store, recognizer)
                .RunAsync(operation.Token, ShowForScreenshotRecognitionAsync);
            ShowIfOpen();

            switch (result)
            {
                case ScreenshotOcrResult.Succeeded success:
                    _resultText.Text = success.Text;
                    _copyButton.IsEnabled = !string.IsNullOrEmpty(success.Text);
                    _statusText.Text = $"{success.State} · {success.JobId:D}";
                    break;
                case ScreenshotOcrResult.Cancelled:
                    _statusText.Text = "Screenshot cancelled.";
                    break;
                case ScreenshotOcrResult.Busy:
                    _statusText.Text = "A screenshot operation is already active.";
                    break;
                case ScreenshotOcrResult.Failed failure when failure.Failure == ScreenshotCaptureFailure.PermissionDenied:
                    _statusText.Text = "Screen Recording permission is required; grant it in System Settings and relaunch NEOCR.";
                    _resultText.Text = failure.Message;
                    break;
                case ScreenshotOcrResult.Failed failure:
                    _statusText.Text = "Screenshot failed.";
                    _resultText.Text = failure.Message;
                    break;
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            ShowIfOpen();
            _statusText.Text = "Cancelled.";
            _resultText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            ShowIfOpen();
            _statusText.Text = "Screenshot OCR failed.";
            _resultText.Text = exception.Message;
        }
        finally
        {
            ShowIfOpen();
            EndOperation(operation);
        }
    }

    private async void PauseResumeClickedAsync(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            if (_activeExecutionService is not null && _activeJobId is { } activeJobId)
            {
                _pauseResumeButton.IsEnabled = false;
                if (await _activeExecutionService.RequestPauseAsync(activeJobId))
                {
                    _statusText.Text = "Pausing after the current page…";
                }

                return;
            }
        }
        catch (Exception exception)
        {
            _statusText.Text = "Pause request failed.";
            _resultText.Text = exception.Message;
            _pauseResumeButton.IsEnabled = _activeJobId is not null;
        }
    }

    private async void CancelClickedAsync(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeOperation is not null)
        {
            _statusText.Text = "Cancelling…";
            _cancelButton.IsEnabled = false;
            _pauseResumeButton.IsEnabled = false;
            _activeOperation.Cancel();
            return;
        }
    }

    private CancellationTokenSource? BeginOperation()
    {
        if (_closing || !_operationGate.Wait(0))
        {
            return null;
        }

        var operation = new CancellationTokenSource();
        _activeOperation = operation;
        _runButton.IsEnabled = false;
        _screenshotButton.IsEnabled = false;
        _pauseResumeButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _refreshJobsButton.IsEnabled = false;
        _resumeSelectedButton.IsEnabled = false;
        _cancelSelectedButton.IsEnabled = false;
        return operation;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_activeOperation, operation))
        {
            _activeOperation = null;
        }

        operation.Dispose();
        UpdateIdleControls();
        _operationGate.Release();
    }

    private async Task ResumeBatchAsync(Guid pausedJobId)
    {
        if (string.IsNullOrWhiteSpace(_pluginDirectory.Text))
        {
            _statusText.Text = "The paused job requires its recognizer plugin.";
            return;
        }

        var operation = BeginOperation();
        if (operation is null)
        {
            return;
        }

        SqliteJobStore? store = null;
        try
        {
            _statusText.Text = "Resuming…";
            store = new SqliteJobStore(GetDefaultDatabasePath());
            await store.InitializeAsync(operation.Token);
            var package = await PluginPackage.LoadAsync(_pluginDirectory.Text, operation.Token);
            var recognizer = await GetRecognizerSessionAsync(package);
            var executionService = new JobExecutionService(store, recognizer);
            _activeExecutionService = executionService;
            _activeJobId = pausedJobId;
            _pauseResumeButton.Content = "Pause";
            _pauseResumeButton.IsEnabled = true;
            var execution = await executionService.ResumeAsync(pausedJobId, operation.Token);
            await PresentBatchResultAsync(execution);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _statusText.Text = "Cancelled.";
            _resultText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            _statusText.Text = "Resume failed.";
            _resultText.Text = exception.Message;
        }
        finally
        {
            _activeExecutionService = null;
            _activeJobId = null;
            try
            {
                if (store is not null)
                {
                    await RefreshPausedJobsAsync(store);
                }
            }
            finally
            {
                EndOperation(operation);
            }
        }
    }

    private async Task PresentBatchResultAsync(JobExecutionResult execution)
    {
        switch (execution)
        {
            case JobExecutionResult.Finished finished:
                _resultText.Text = await File.ReadAllTextAsync(finished.Pipeline.OutputPath);
                _copyButton.IsEnabled = !string.IsNullOrEmpty(_resultText.Text);
                _statusText.Text = $"{finished.State} · {finished.JobId:D}";
                break;
            case JobExecutionResult.Paused paused:
                _statusText.Text = $"Paused · {paused.CompletedPages}/{paused.TotalPages} pages · {paused.JobId:D}";
                break;
        }

        var store = new SqliteJobStore(GetDefaultDatabasePath());
        await store.InitializeAsync();
        await RefreshPausedJobsAsync(store);
    }

    private void UpdateIdleControls()
    {
        _runButton.IsEnabled = !_closing;
        _screenshotButton.IsEnabled = !_closing && _screenshotService is not null;
        _pauseResumeButton.Content = "Pause";
        _pauseResumeButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        _refreshJobsButton.IsEnabled = !_closing;
        UpdateSelectedJobControls();
    }

    private void PausedJobSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs) =>
        UpdateSelectedJobControls();

    private async void RefreshJobsClickedAsync(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var store = new SqliteJobStore(GetDefaultDatabasePath());
            await store.InitializeAsync();
            var recovered = await new JobRecoveryService(store).ReconcileAbandonedAsync();
            await RefreshPausedJobsAsync(store);
            _statusText.Text = recovered.Count == 0
                ? "Paused tasks refreshed."
                : $"Recovered {recovered.Count} interrupted task(s).";
        }
        catch (Exception exception)
        {
            _statusText.Text = "Task refresh failed.";
            _resultText.Text = exception.Message;
        }
    }

    private async void ResumeSelectedClickedAsync(object? sender, RoutedEventArgs eventArgs)
    {
        if (_pausedJobs.SelectedItem is PausedJobItem selected)
        {
            await ResumeBatchAsync(selected.Id);
        }
    }

    private async void CancelSelectedClickedAsync(object? sender, RoutedEventArgs eventArgs)
    {
        if (_pausedJobs.SelectedItem is not PausedJobItem selected)
        {
            return;
        }

        var operation = BeginOperation();
        if (operation is null)
        {
            _statusText.Text = "Another OCR operation is already active.";
            return;
        }

        _cancelButton.IsEnabled = false;
        try
        {
            var store = new SqliteJobStore(GetDefaultDatabasePath());
            await store.InitializeAsync(operation.Token);
            if (!await new JobRecoveryService(store).CancelAsync(selected.Id, operation.Token))
            {
                throw new InvalidOperationException($"Task '{selected.Id}' is no longer paused.");
            }

            _statusText.Text = $"Cancelled · {selected.Id:D}";
            await RefreshPausedJobsAsync(store);
        }
        catch (Exception exception)
        {
            _statusText.Text = "Cancel failed.";
            _resultText.Text = exception.Message;
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async Task RefreshPausedJobsAsync(
        SqliteJobStore store,
        CancellationToken cancellationToken = default)
    {
        var selectedId = (_pausedJobs.SelectedItem as PausedJobItem)?.Id;
        var jobs = await new JobRecoveryService(store).ListPausedAsync(cancellationToken);
        var items = new List<PausedJobItem>(jobs.Count);
        foreach (var job in jobs)
        {
            var pages = await store.GetPagesAsync(job.Id, cancellationToken);
            var completed = pages.Count(page => page.State is not PageCheckpointState.Pending);
            var recognizer = job.Recognizer is null
                ? "unknown recognizer"
                : $"{job.Recognizer.Id}@{job.Recognizer.Version}";
            items.Add(new PausedJobItem(
                job.Id,
                $"{job.Id:D} · {completed}/{job.Spec.Inputs.Count} · {recognizer}"));
        }

        _pausedJobs.ItemsSource = items;
        _pausedJobs.SelectedItem = items.FirstOrDefault(item => item.Id == selectedId)
            ?? items.FirstOrDefault();
        UpdateSelectedJobControls();
    }

    private void UpdateSelectedJobControls()
    {
        var canEditSelection = !_closing
            && _activeOperation is null
            && _pausedJobs.SelectedItem is PausedJobItem;
        _resumeSelectedButton.IsEnabled = canEditSelection;
        _cancelSelectedButton.IsEnabled = canEditSelection;
    }

    private async Task<WorkerRecognizerSession> GetRecognizerSessionAsync(PluginPackage package)
    {
        if (_recognizerSession is not null
            && string.Equals(
                _recognizerDirectory,
                package.Directory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return _recognizerSession;
        }

        if (_recognizerSession is not null)
        {
            await _recognizerSession.DisposeAsync();
        }

        _recognizerDirectory = package.Directory;
        _recognizerSession = package.CreateRecognizerSession();
        return _recognizerSession;
    }

    private async ValueTask ShowForScreenshotRecognitionAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowIfOpen();
            _statusText.Text = "Recognizing screenshot…";
        });
    }

    private void ShowIfOpen()
    {
        if (_closing)
        {
            return;
        }

        Show();
        Activate();
    }

    private async void CopyClickedAsync(object? sender, RoutedEventArgs eventArgs)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(_resultText.Text))
        {
            return;
        }

        await clipboard.SetValueAsync(DataFormat.Text, _resultText.Text);
        _statusText.Text = "Result copied.";
    }

    private static string GetDefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OcrWorkbench",
        "jobs.db");

    private static string GetDefaultSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OcrWorkbench",
        "settings.json");

    private sealed record PausedJobItem(Guid Id, string Display)
    {
        public override string ToString() => Display;
    }
}
