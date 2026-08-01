using System.Text.Json;
using OcrWorkbench.Contracts;
using OcrWorkbench.Core;
using OcrWorkbench.Infrastructure;
using OcrWorkbench.PluginHost;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (!CliArguments.TryParse(args, out var options, out var error))
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine(CliArguments.Usage);
        return 2;
    }

    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        var job = new JobSpec(
            options!.Images.Select(path => new ImageInput(path, GuessMimeType(path))).ToArray(),
            new RecognitionOptions(options.Language),
            new PlainTextExportOptions(options.OutputPath));
        var store = new SqliteJobStore(options.DatabasePath ?? GetDefaultDatabasePath());
        await store.InitializeAsync(cancellation.Token);
        var package = await PluginPackage.LoadAsync(options!.PluginDirectory, cancellation.Token);
        await using var recognizer = await package.StartRecognizerAsync(
            line => Console.Error.WriteLine($"worker: {line}"),
            cancellation.Token);
        var execution = await new JobExecutionService(store, recognizer)
            .ExecuteAsync(job, cancellation.Token);
        var result = execution.Pipeline;

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Completed {result.Completed.Count} image(s); declined {result.Declined.Count}.");
            Console.WriteLine($"Job: {execution.JobId:D}");
            Console.WriteLine(result.OutputPath);
        }

        return result.Declined.Count == 0 ? 0 : 3;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine("Cancelled.");
        return 130;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static string GetDefaultDatabasePath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "OcrWorkbench",
    "jobs.db");

static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
    ".webp" => "image/webp",
    ".bmp" => "image/bmp",
    ".tif" or ".tiff" => "image/tiff",
    _ => "application/octet-stream",
};

sealed record CliArguments(
    string PluginDirectory,
    string OutputPath,
    string Language,
    string? DatabasePath,
    bool Json,
    IReadOnlyList<string> Images)
{
    public const string Usage = "Usage: ocr images --plugin <directory> --output <path> [--language <tag>] [--database <path>] [--json] <image>...";

    public static bool TryParse(string[] args, out CliArguments? parsed, out string error)
    {
        parsed = null;
        error = string.Empty;
        if (args.Length == 0 || !string.Equals(args[0], "images", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only the 'images' command is available in Phase 0.";
            return false;
        }

        string? plugin = null;
        string? output = null;
        var language = "auto";
        string? database = null;
        var json = false;
        var images = new List<string>();

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--plugin" when index + 1 < args.Length:
                    plugin = args[++index];
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--language" when index + 1 < args.Length:
                    language = args[++index];
                    break;
                case "--database" when index + 1 < args.Length:
                    database = args[++index];
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    if (args[index].StartsWith('-'))
                    {
                        error = $"Unknown or incomplete option: {args[index]}";
                        return false;
                    }

                    images.Add(args[index]);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(output) || images.Count == 0)
        {
            error = "--plugin, --output and at least one image are required.";
            return false;
        }

        parsed = new CliArguments(plugin, output, language, database, json, images);
        return true;
    }
}
