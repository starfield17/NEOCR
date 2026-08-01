using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public sealed class ScreenshotOcrWorkflow(
    IInteractiveScreenshotService screenshotService,
    IJobStore jobStore,
    IRecognizer recognizer,
    string? temporaryRoot = null)
{
    private readonly SemaphoreSlim _singleCapture = new(1, 1);
    private readonly string _temporaryRoot = temporaryRoot ?? Path.Combine(Path.GetTempPath(), "NEOCR");

    public async Task<ScreenshotOcrResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!await _singleCapture.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new ScreenshotOcrResult.Busy();
        }

        string? operationDirectory = null;
        try
        {
            var capture = await screenshotService.CaptureRegionAsync(cancellationToken).ConfigureAwait(false);
            switch (capture)
            {
                case ScreenshotCaptureResult.Cancelled:
                    return new ScreenshotOcrResult.Cancelled();
                case ScreenshotCaptureResult.Failed failure:
                    return new ScreenshotOcrResult.Failed(failure.Failure, failure.Message);
                case ScreenshotCaptureResult.Succeeded success:
                    if (success.PngBytes.Length == 0 || success.PixelWidth <= 0 || success.PixelHeight <= 0)
                    {
                        return new ScreenshotOcrResult.Failed(
                            ScreenshotCaptureFailure.NativeFailure,
                            "The screenshot service returned an empty image.");
                    }

                    operationDirectory = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(operationDirectory);
                    var inputPath = Path.Combine(operationDirectory, "capture.png");
                    var outputPath = Path.Combine(operationDirectory, "result.txt");
                    await File.WriteAllBytesAsync(inputPath, success.PngBytes, cancellationToken).ConfigureAwait(false);

                    var job = new JobSpec(
                        [new ImageInput(inputPath, "image/png")],
                        new RecognitionOptions(),
                        new PlainTextExportOptions(outputPath));
                    var execution = await new JobExecutionService(jobStore, recognizer)
                        .ExecuteAsync(job, cancellationToken)
                        .ConfigureAwait(false);
                    var text = await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false);
                    return new ScreenshotOcrResult.Succeeded(execution.JobId, execution.State, text);
                default:
                    throw new InvalidOperationException("The screenshot service returned an unknown result.");
            }
        }
        finally
        {
            if (operationDirectory is not null && Directory.Exists(operationDirectory))
            {
                Directory.Delete(operationDirectory, recursive: true);
            }

            _singleCapture.Release();
        }
    }
}

public abstract record ScreenshotOcrResult
{
    private ScreenshotOcrResult() { }

    public sealed record Succeeded(Guid JobId, JobState State, string Text) : ScreenshotOcrResult;

    public sealed record Cancelled : ScreenshotOcrResult;

    public sealed record Busy : ScreenshotOcrResult;

    public sealed record Failed(ScreenshotCaptureFailure Failure, string Message) : ScreenshotOcrResult;
}
