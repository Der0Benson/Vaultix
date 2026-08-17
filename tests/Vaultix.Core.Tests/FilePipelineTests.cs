using System.Security.Cryptography;
using System.Text;
using Vaultix.Infrastructure;

namespace Vaultix.Core.Tests;

public sealed class FilePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "VaultixTests", Guid.NewGuid().ToString("N"));

    public FilePipelineTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task HasherProducesSha256WithoutLoadingContractIntoMemory()
    {
        var path = Path.Combine(_root, "content.bin");
        var content = Encoding.UTF8.GetBytes("Vaultix restores what it protects.");
        await File.WriteAllBytesAsync(path, content);

        var hash = await new Sha256FileHasher().HashAsync(path, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), hash);
    }

    [Fact]
    public async Task ScannerExcludesContextualBuildArtifactsButKeepsOrdinaryBinFolders()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "Project"));
        await File.WriteAllTextAsync(Path.Combine(project.FullName, "Project.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(project.FullName, "bin"));
        await File.WriteAllTextAsync(Path.Combine(project.FullName, "bin", "generated.dll"), "generated");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(_root, "bin", "important.txt"), "important");

        var scanner = new FileScanner(new DefaultExcludePolicy());
        var files = new List<string>();
        await foreach (var file in scanner.ScanAsync(_root, CancellationToken.None))
        {
            files.Add(file.RelativePath);
        }

        Assert.Contains(Path.Combine("bin", "important.txt"), files);
        Assert.DoesNotContain(Path.Combine("Project", "bin", "generated.dll"), files);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
