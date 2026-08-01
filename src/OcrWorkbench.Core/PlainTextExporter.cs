using System.Text;
using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public static class PlainTextExporter
{
    public static async Task WriteAtomicallyAsync(
        string outputPath,
        IReadOnlyList<PageRecognition> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Output path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                for (var index = 0; index < pages.Count; index++)
                {
                    if (index > 0)
                    {
                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    await writer.WriteLineAsync($"===== {pages[index].Page.SourcePath} =====").ConfigureAwait(false);
                    foreach (var block in pages[index].Result.SpatialBlocks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(block.Text).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(pages[index].Result.SemanticMarkdown))
                    {
                        await writer.WriteLineAsync(pages[index].Result.SemanticMarkdown).ConfigureAwait(false);
                    }
                }
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed record PageRecognition(PageArtifact Page, RecognitionResult Result);

