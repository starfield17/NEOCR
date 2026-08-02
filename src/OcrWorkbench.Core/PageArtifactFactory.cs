using System.Security.Cryptography;
using System.Text;
using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

internal static class PageArtifactFactory
{
    public static PageArtifact Create(ImageInput input, int inputIndex)
    {
        var fullPath = Path.GetFullPath(input.Path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Input image does not exist.", fullPath);
        }

        var file = new FileInfo(fullPath);
        var identity = $"{fullPath}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}";
        var stableId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return new PageArtifact(stableId, fullPath, input.MimeType, inputIndex + 1);
    }
}
