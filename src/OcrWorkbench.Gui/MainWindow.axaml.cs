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
    private readonly Button _copyButton;
    private readonly TextBlock _statusText;
    private readonly TextBlock _hotkeyText;
    private readonly TextBox _resultText;
    private readonly GuiSettingsStore _settings = new(GetDefaultSettingsPath());
    private readonly IInteractiveScreenshotService? _screenshotService;
    private readonly IScreenCapturePermissionService? _screenCapturePermissionService;
    private readonly IGlobalHotkeyService? _hotkeyService;
    private int _screenshotRunning;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _pluginDirectory = this.FindControl<TextBox>("PluginDirectory")!;
        _imagePaths = this.FindControl<TextBox>("ImagePaths")!;
        _outputPath = this.FindControl<TextBox>("OutputPath")!;
        _runButton = this.FindControl<Button>("RunButton")!;
        _screenshotButton = this.FindControl<Button>("ScreenshotButton")!;
        _copyButton = this.FindControl<Button>("CopyButton")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _hotkeyText = this.FindControl<TextBlock>("HotkeyText")!;
        _resultText = this.FindControl<TextBox>("ResultText")!;
        _runButton.Click += RunClickedAsync;
        _screenshotButton.Click += ScreenshotClickedAsync;
        _copyButton.Click += CopyClickedAsync;

        if (OperatingSystem.IsMacOSVersionAtLeast(15, 2))
        {
            var screenshotService = new MacOSInteractiveScreenshotService();
            _screenshotService = screenshotService;
            _screenCapturePermissionService = screenshotService;
            _hotkeyService = new MacOSGlobalHotkeyService();
            _hotkeyService.Pressed += HotkeyPressed;
            Opened += WindowOpenedAsync;
            Closed += WindowClosed;
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

        _runButton.IsEnabled = false;
        _statusText.Text = "Running…";
        _resultText.Text = string.Empty;
        try
        {
            var store = new SqliteJobStore(GetDefaultDatabasePath());
            await store.InitializeAsync();
            var package = await PluginPackage.LoadAsync(_pluginDirectory.Text);
            await _settings.SaveAsync(new GuiSettings(_pluginDirectory.Text));
            await using var recognizer = await package.StartRecognizerAsync();
            var job = new JobSpec(
                images.Select(path => new ImageInput(path)).ToArray(),
                new RecognitionOptions(),
                new PlainTextExportOptions(_outputPath.Text));
            var execution = await new JobExecutionService(store, recognizer).ExecuteAsync(job);
            _resultText.Text = await File.ReadAllTextAsync(execution.Pipeline.OutputPath);
            _copyButton.IsEnabled = !string.IsNullOrEmpty(_resultText.Text);
            _statusText.Text = $"{execution.State} · {execution.JobId:D}";
        }
        catch (Exception exception)
        {
            _statusText.Text = "Failed";
            _resultText.Text = exception.Message;
        }
        finally
        {
            _runButton.IsEnabled = true;
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

            await _hotkeyService!.RegisterAsync(HotkeyGesture.DefaultScreenshot);
        }
        catch (Exception exception)
        {
            _statusText.Text = $"Shortcut unavailable: {exception.Message}";
        }
    }

    private void WindowClosed(object? sender, EventArgs eventArgs)
    {
        if (_hotkeyService is null)
        {
            return;
        }

        _hotkeyService.Pressed -= HotkeyPressed;
        _ = _hotkeyService.DisposeAsync();
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

        if (Interlocked.Exchange(ref _screenshotRunning, 1) != 0)
        {
            _statusText.Text = "A screenshot operation is already active.";
            return;
        }

        try
        {
            _statusText.Text = "Checking Screen Recording permission…";
            var permission = await _screenCapturePermissionService!.RequestAccessAsync();
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
            await store.InitializeAsync();
            var package = await PluginPackage.LoadAsync(_pluginDirectory.Text);
            await _settings.SaveAsync(new GuiSettings(_pluginDirectory.Text));
            await using var recognizer = await package.StartRecognizerAsync();

            Hide();
            var result = await new ScreenshotOcrWorkflow(_screenshotService, store, recognizer).RunAsync();
            Show();
            Activate();

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
        catch (Exception exception)
        {
            Show();
            Activate();
            _statusText.Text = "Screenshot OCR failed.";
            _resultText.Text = exception.Message;
        }
        finally
        {
            _screenshotButton.IsEnabled = _screenshotService is not null;
            Interlocked.Exchange(ref _screenshotRunning, 0);
        }
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
}
