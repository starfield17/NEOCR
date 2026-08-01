using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OcrWorkbench.Contracts;
using OcrWorkbench.Core;
using OcrWorkbench.Infrastructure;
using OcrWorkbench.PluginHost;

namespace OcrWorkbench.Gui;

public sealed partial class MainWindow : Window
{
    private readonly TextBox _pluginDirectory;
    private readonly TextBox _imagePaths;
    private readonly TextBox _outputPath;
    private readonly Button _runButton;
    private readonly TextBlock _statusText;
    private readonly TextBox _resultText;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _pluginDirectory = this.FindControl<TextBox>("PluginDirectory")!;
        _imagePaths = this.FindControl<TextBox>("ImagePaths")!;
        _outputPath = this.FindControl<TextBox>("OutputPath")!;
        _runButton = this.FindControl<Button>("RunButton")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _resultText = this.FindControl<TextBox>("ResultText")!;
        _runButton.Click += RunClickedAsync;
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
            await using var recognizer = await package.StartRecognizerAsync();
            var job = new JobSpec(
                images.Select(path => new ImageInput(path)).ToArray(),
                new RecognitionOptions(),
                new PlainTextExportOptions(_outputPath.Text));
            var execution = await new JobExecutionService(store, recognizer).ExecuteAsync(job);
            _resultText.Text = await File.ReadAllTextAsync(execution.Pipeline.OutputPath);
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

    private static string GetDefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OcrWorkbench",
        "jobs.db");
}

