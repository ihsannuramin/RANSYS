namespace Ransys.Adapter.Tests;

/// <summary>The compiled proto must stay identical (modulo line endings) to the approved contract document.</summary>
public sealed class ProtoBaselineTests
{
    [Fact]
    public void Compiled_proto_is_the_contract_document_unchanged()
    {
        var root = RepositoryRoot();
        var reference = File.ReadAllText(Path.Combine(root, "docs", "RANSYS_Provider_Adapter_v1.proto"));
        var compiled = File.ReadAllText(Path.Combine(root, "src", "Ransys.Adapter.Contracts", "Protos", "ransys_provider_adapter_v1.proto"));

        Assert.Equal(Normalize(reference), Normalize(compiled));
        Assert.Contains("option csharp_namespace = \"Ransys.Provider.V1\";", compiled, StringComparison.Ordinal);
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n").TrimEnd();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ransys.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Ransys.sln not found.");
    }
}
