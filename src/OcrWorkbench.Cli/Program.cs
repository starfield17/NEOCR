using System.Text.Json;
using OcrWorkbench.Contracts;
using OcrWorkbench.Core;
using OcrWorkbench.Infrastructure;
using OcrWorkbench.PluginHost;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (!CliArguments.TryParse(args, out var command, out var error))
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
        var store = new SqliteJobStore(command!.DatabasePath ?? GetDefaultDatabasePath());
        await store.InitializeAsync(cancellation.Token);
        var recovery = new JobRecoveryService(store);
        var recovered = await recovery.ReconcileAbandonedAsync(cancellation.Token);

        return command switch
        {
            ImagesCommand images => await RunImagesAsync(images, store, cancellation.Token),
            JobsListCommand list => await ListJobsAsync(list, recovery, store, cancellation.Token),
            JobsRecoverCommand recover => PrintRecovered(recover, recovered),
            JobsResumeCommand resume => await ResumeJobAsync(resume, store, cancellation.Token),
            JobsCancelCommand cancel => await CancelJobAsync(cancel, recovery, cancellation.Token),
            _ => throw new InvalidOperationException("Unknown command."),
        };
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

static async Task<int> RunImagesAsync(
    ImagesCommand options,
    SqliteJobStore store,
    CancellationToken cancellationToken)
{
    var job = new JobSpec(
        options.Images.Select(path => new ImageInput(path, GuessMimeType(path))).ToArray(),
        new RecognitionOptions(options.Language),
        new PlainTextExportOptions(options.OutputPath));
    await using var recognizer = await StartRecognizerAsync(options.PluginDirectory, cancellationToken);
    var execution = await new JobExecutionService(store, recognizer).ExecuteAsync(job, cancellationToken);
    return PrintFinished(execution, options.Json);
}

static async Task<int> ResumeJobAsync(
    JobsResumeCommand options,
    SqliteJobStore store,
    CancellationToken cancellationToken)
{
    await using var recognizer = await StartRecognizerAsync(options.PluginDirectory, cancellationToken);
    var execution = await new JobExecutionService(store, recognizer)
        .ResumeAsync(options.JobId, cancellationToken);
    return PrintFinished(execution, options.Json);
}

static async Task<WorkerProcessRecognizer> StartRecognizerAsync(
    string pluginDirectory,
    CancellationToken cancellationToken)
{
    var package = await PluginPackage.LoadAsync(pluginDirectory, cancellationToken);
    return await package.StartRecognizerAsync(
        line => Console.Error.WriteLine($"worker: {line}"),
        cancellationToken);
}

static int PrintFinished(JobExecutionResult execution, bool json)
{
    var finished = execution as JobExecutionResult.Finished
        ?? throw new InvalidOperationException("No external pause request was expected for this CLI job.");
    var result = finished.Pipeline;
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOutput.Options));
    }
    else
    {
        Console.WriteLine($"Completed {result.Completed.Count} image(s); declined {result.Declined.Count}.");
        Console.WriteLine($"Job: {finished.JobId:D}");
        Console.WriteLine(result.OutputPath);
    }

    return result.Declined.Count == 0 ? 0 : 3;
}

static async Task<int> ListJobsAsync(
    JobsListCommand options,
    JobRecoveryService recovery,
    SqliteJobStore store,
    CancellationToken cancellationToken)
{
    var jobs = await recovery.ListPausedAsync(cancellationToken);
    var rows = new List<object>(jobs.Count);
    foreach (var job in jobs)
    {
        var pages = await store.GetPagesAsync(job.Id, cancellationToken);
        rows.Add(new
        {
            jobId = job.Id,
            state = job.State.ToString(),
            completedPages = pages.Count(page => page.State is not PageCheckpointState.Pending),
            totalPages = job.Spec.Inputs.Count,
            updatedAt = job.UpdatedAt,
            outputPath = job.Spec.Export.OutputPath,
            recognizer = job.Recognizer,
        });
    }

    if (options.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(rows, JsonOutput.Options));
    }
    else if (rows.Count == 0)
    {
        Console.WriteLine("No paused tasks.");
    }
    else
    {
        foreach (var job in jobs)
        {
            var pages = await store.GetPagesAsync(job.Id, cancellationToken);
            var completed = pages.Count(page => page.State is not PageCheckpointState.Pending);
            Console.WriteLine($"{job.Id:D}  {completed}/{job.Spec.Inputs.Count}  {job.Recognizer?.Id}@{job.Recognizer?.Version}");
            Console.WriteLine($"  {job.Spec.Export.OutputPath}");
        }
    }

    return 0;
}

static int PrintRecovered(JobsRecoverCommand options, IReadOnlyList<Guid> recovered)
{
    if (options.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { recovered }, JsonOutput.Options));
    }
    else
    {
        Console.WriteLine($"Recovered {recovered.Count} interrupted task(s).");
        foreach (var jobId in recovered)
        {
            Console.WriteLine(jobId.ToString("D"));
        }
    }

    return 0;
}

static async Task<int> CancelJobAsync(
    JobsCancelCommand options,
    JobRecoveryService recovery,
    CancellationToken cancellationToken)
{
    if (!await recovery.CancelAsync(options.JobId, cancellationToken))
    {
        throw new InvalidOperationException($"Task '{options.JobId}' is neither queued nor paused.");
    }

    if (options.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new { jobId = options.JobId, cancelled = true },
            JsonOutput.Options));
    }
    else
    {
        Console.WriteLine($"Cancelled {options.JobId:D}.");
    }

    return 0;
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

abstract record CliCommand(string? DatabasePath, bool Json);

sealed record ImagesCommand(
    string PluginDirectory,
    string OutputPath,
    string Language,
    string? DatabasePath,
    bool Json,
    IReadOnlyList<string> Images) : CliCommand(DatabasePath, Json);

sealed record JobsListCommand(string? DatabasePath, bool Json) : CliCommand(DatabasePath, Json);

sealed record JobsRecoverCommand(string? DatabasePath, bool Json) : CliCommand(DatabasePath, Json);

sealed record JobsResumeCommand(
    Guid JobId,
    string PluginDirectory,
    string? DatabasePath,
    bool Json) : CliCommand(DatabasePath, Json);

sealed record JobsCancelCommand(Guid JobId, string? DatabasePath, bool Json) : CliCommand(DatabasePath, Json);

static class JsonOutput
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true };
}

static class CliArguments
{
    public const string Usage = """
        Usage:
          ocr images --plugin <directory> --output <path> [--language <tag>] [--database <path>] [--json] <image>...
          ocr jobs list [--database <path>] [--json]
          ocr jobs recover [--database <path>] [--json]
          ocr jobs resume <job-id> --plugin <directory> [--database <path>] [--json]
          ocr jobs cancel <job-id> [--database <path>] [--json]
        """;

    public static bool TryParse(string[] args, out CliCommand? parsed, out string error)
    {
        parsed = null;
        error = string.Empty;
        if (args.Length == 0)
        {
            error = "A command is required.";
            return false;
        }

        return args[0].ToLowerInvariant() switch
        {
            "images" => TryParseImages(args, out parsed, out error),
            "jobs" => TryParseJobs(args, out parsed, out error),
            _ => Fail("Expected 'images' or 'jobs'.", out error),
        };
    }

    private static bool TryParseImages(string[] args, out CliCommand? parsed, out string error)
    {
        parsed = null;
        string? plugin = null;
        string? output = null;
        string? database = null;
        var language = "auto";
        var json = false;
        var images = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--plugin" when TryTakeValue(args, ref index, out var value):
                    plugin = value;
                    break;
                case "--output" when TryTakeValue(args, ref index, out var value):
                    output = value;
                    break;
                case "--language" when TryTakeValue(args, ref index, out var value):
                    language = value;
                    break;
                case "--database" when TryTakeValue(args, ref index, out var value):
                    database = value;
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    if (args[index].StartsWith('-'))
                    {
                        return Fail($"Unknown or incomplete option: {args[index]}", out error);
                    }

                    images.Add(args[index]);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(output) || images.Count == 0)
        {
            return Fail("--plugin, --output and at least one image are required.", out error);
        }

        parsed = new ImagesCommand(plugin, output, language, database, json, images);
        error = string.Empty;
        return true;
    }

    private static bool TryParseJobs(string[] args, out CliCommand? parsed, out string error)
    {
        parsed = null;
        if (args.Length < 2)
        {
            return Fail("A jobs subcommand is required.", out error);
        }

        var verb = args[1].ToLowerInvariant();
        Guid? jobId = null;
        var index = 2;
        if (verb is "resume" or "cancel")
        {
            if (index >= args.Length || !Guid.TryParse(args[index++], out var value))
            {
                return Fail($"jobs {verb} requires a valid job ID.", out error);
            }

            jobId = value;
        }

        string? plugin = null;
        string? database = null;
        var json = false;
        for (; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--plugin" when TryTakeValue(args, ref index, out var value):
                    plugin = value;
                    break;
                case "--database" when TryTakeValue(args, ref index, out var value):
                    database = value;
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    return Fail($"Unknown or incomplete option: {args[index]}", out error);
            }
        }

        parsed = verb switch
        {
            "list" => new JobsListCommand(database, json),
            "recover" => new JobsRecoverCommand(database, json),
            "resume" when !string.IsNullOrWhiteSpace(plugin) =>
                new JobsResumeCommand(jobId!.Value, plugin, database, json),
            "cancel" => new JobsCancelCommand(jobId!.Value, database, json),
            "resume" => null,
            _ => null,
        };
        if (parsed is null)
        {
            return Fail(verb == "resume"
                ? "jobs resume requires --plugin <directory>."
                : $"Unknown jobs subcommand: {verb}", out error);
        }

        error = string.Empty;
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
