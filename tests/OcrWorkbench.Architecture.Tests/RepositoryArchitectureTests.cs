using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OcrWorkbench.Architecture.Tests;

public sealed partial class RepositoryArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProductionProjects_follow_the_allowed_dependency_graph()
    {
        var allowed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["OcrWorkbench.Contracts"] = [],
            ["OcrWorkbench.Core"] = ["OcrWorkbench.Contracts"],
            ["OcrWorkbench.Infrastructure"] = ["OcrWorkbench.Contracts", "OcrWorkbench.Core"],
            ["OcrWorkbench.PluginHost"] = ["OcrWorkbench.Contracts", "OcrWorkbench.Core"],
            ["OcrWorkbench.Cli"] = ["OcrWorkbench.Contracts", "OcrWorkbench.Core", "OcrWorkbench.Infrastructure", "OcrWorkbench.PluginHost"],
            ["OcrWorkbench.Gui"] = ["OcrWorkbench.Contracts", "OcrWorkbench.Core", "OcrWorkbench.Infrastructure", "OcrWorkbench.PluginHost", "OcrWorkbench.Platform.MacOS"],
            ["OcrWorkbench.Platform.MacOS"] = ["OcrWorkbench.Core"],
            ["OcrWorkbench.FakeWorker"] = ["OcrWorkbench.Contracts"],
        };

        var projects = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "workers"), "*.csproj", SearchOption.AllDirectories));

        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            Assert.True(allowed.TryGetValue(name, out var allowedReferences), $"Project '{name}' is missing from the dependency policy.");

            var document = XDocument.Load(project);
            var references = document.Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFileNameWithoutExtension(value!));

            foreach (var reference in references)
            {
                Assert.Contains(reference, allowedReferences!);
            }
        }
    }

    [Fact]
    public void Required_handoff_documents_exist_and_links_resolve()
    {
        string[] required =
        [
            "SPEC.md",
            "docs/architecture/README.md",
            "docs/architecture/system.md",
            "docs/handoff/CURRENT.md",
            "docs/handoff/session-workflow.md",
            "docs/platforms/macos.md",
            "docs/platforms/windows.md",
        ];

        foreach (var path in required)
        {
            Assert.True(File.Exists(Path.Combine(RepositoryRoot, path)), $"Required document is missing: {path}");
        }

        foreach (var documentPath in Directory.EnumerateFiles(RepositoryRoot, "*.md", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var content = File.ReadAllText(documentPath);
            Assert.DoesNotContain("/Users/ray/", content, StringComparison.Ordinal);

            foreach (Match match in MarkdownLink().Matches(content))
            {
                var target = match.Groups["target"].Value;
                if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith('#'))
                {
                    continue;
                }

                var withoutFragment = target.Split('#', 2)[0];
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(documentPath)!, Uri.UnescapeDataString(withoutFragment)));
                Assert.True(File.Exists(resolved) || Directory.Exists(resolved), $"Broken Markdown link in {Path.GetRelativePath(RepositoryRoot, documentPath)}: {target}");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OcrWorkbench.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"\[[^\]]+\]\((?<target>[^)]+)\)")]
    private static partial Regex MarkdownLink();
}
