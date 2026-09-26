using System.Xml.Linq;

namespace Ransys.IntegrationTests.Architecture;

/// <summary>
/// Enforces the dependency direction from main.md §8:
/// Domain ← Application ← Infrastructure / API.
/// Checked against project files so a forbidden reference fails even before it is used in code.
/// </summary>
public sealed class DependencyRulesTests
{
    private static readonly string[] ForbiddenDomainPackagePrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Dapper",
        "RabbitMQ",
        "StackExchange.Redis",
        "Microsoft.Extensions.Http",
        "System.Net.Http",
    ];

    [Fact]
    public void Domain_has_no_project_package_or_framework_references()
    {
        var project = LoadProject("Ransys.Domain");

        Assert.Empty(ProjectReferences(project));
        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.DoesNotContain(
            PackageReferences(project),
            package => ForbiddenDomainPackagePrefixes.Any(prefix =>
                package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Microsoft.NET.Sdk", project.Root!.Attribute("Sdk")!.Value);
    }

    [Fact]
    public void Domain_assembly_does_not_load_infrastructure_assemblies()
    {
        var domain = typeof(Ransys.Domain.DomainAssembly).Assembly;

        var referenced = domain.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        Assert.DoesNotContain(
            referenced,
            name => ForbiddenDomainPackagePrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("Ransys.Application", new[] { "Ransys.Domain" })]
    [InlineData("Ransys.Contracts", new[] { "Ransys.Domain" })]
    [InlineData("Ransys.Adapter.Contracts", new[] { "Ransys.Contracts" })]
    [InlineData("Ransys.Adapter.Sdk", new[] { "Ransys.Adapter.Contracts" })]
    public void Inner_projects_reference_only_allowed_projects(string projectName, string[] allowed)
    {
        var references = ProjectReferences(LoadProject(projectName));

        Assert.All(references, reference => Assert.Contains(reference, allowed));
    }

    [Theory]
    [InlineData("Ransys.Domain")]
    [InlineData("Ransys.Application")]
    [InlineData("Ransys.Contracts")]
    [InlineData("Ransys.Ledger")]
    [InlineData("Ransys.Routing")]
    [InlineData("Ransys.Configuration")]
    [InlineData("Ransys.TransactionCore")]
    [InlineData("Ransys.Adapter.Contracts")]
    [InlineData("Ransys.Adapter.Sdk")]
    public void Core_projects_do_not_reference_persistence_or_hosts(string projectName)
    {
        var references = ProjectReferences(LoadProject(projectName));

        Assert.DoesNotContain("Ransys.Persistence.PostgreSql", references);
        Assert.DoesNotContain("Ransys.Infrastructure", references);
        Assert.DoesNotContain("Ransys.Api", references);
        Assert.DoesNotContain("Ransys.Workers", references);
    }

    [Theory]
    [InlineData("Ransys.Adapter.Contracts")]
    [InlineData("Ransys.Adapter.Sdk")]
    public void Adapters_cannot_reach_ledger_or_transaction_core(string projectName)
    {
        // Architecture Spec §16 / invariant 10: adapters never mutate wallet or ledger.
        var references = ProjectReferences(LoadProject(projectName));

        Assert.DoesNotContain("Ransys.Ledger", references);
        Assert.DoesNotContain("Ransys.TransactionCore", references);
        Assert.DoesNotContain("Ransys.Persistence.PostgreSql", references);
    }

    [Fact]
    public void Transaction_core_calls_adapters_only_through_the_semantic_contract()
    {
        // M12c: the provider gateway depends on Adapter.Contracts (IProviderAdapter, V1 records). Transport bindings
        // (Adapter.Sdk: gRPC client/server) are wired by the hosts, so Core stays independent of the wire protocol.
        var references = ProjectReferences(LoadProject("Ransys.TransactionCore"));

        Assert.Contains("Ransys.Adapter.Contracts", references);
        Assert.DoesNotContain("Ransys.Adapter.Sdk", references);
    }

    [Theory]
    [InlineData("Ransys.Adapter.Contracts")]
    [InlineData("Ransys.Adapter.Sdk")]
    public void Adapters_cannot_reach_core_or_infrastructure_even_transitively(string projectName)
    {
        // Provider Adapter Contract v1 §6: adapters own protocol/mapping only, never wallet, ledger or routing.
        var closure = TransitiveProjectReferences(projectName);

        Assert.All(closure, reference => Assert.Contains(reference, AllowedAdapterClosure));
    }

    [Theory]
    [InlineData("Ransys.Adapter.Contracts")]
    [InlineData("Ransys.Adapter.Sdk")]
    public void Adapter_libraries_do_not_use_hosting_or_data_access_packages(string projectName)
    {
        // The gRPC server base only needs Grpc.Core.Api; ASP.NET Core hosting belongs to the adapter host process.
        var project = LoadProject(projectName);

        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.DoesNotContain(
            PackageReferences(project),
            package => ForbiddenDomainPackagePrefixes.Any(prefix =>
                package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly string[] AllowedAdapterClosure =
    [
        "Ransys.Adapter.Contracts",
        "Ransys.Contracts",
        "Ransys.Domain",
    ];

    private static HashSet<string> TransitiveProjectReferences(string projectName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(ProjectReferences(LoadProject(projectName)));
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (seen.Add(next))
            {
                foreach (var reference in ProjectReferences(LoadProject(next)))
                {
                    pending.Push(reference);
                }
            }
        }

        return seen;
    }

    private static XDocument LoadProject(string projectName)
    {
        var path = Path.Combine(RepositoryRoot(), "src", projectName, $"{projectName}.csproj");
        return XDocument.Load(path);
    }

    private static List<string> ProjectReferences(XDocument project) =>
        project.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value))
            .ToList();

    private static List<string> PackageReferences(XDocument project) =>
        project.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")!.Value)
            .ToList();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ransys.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Ransys.sln above the test output directory.");
    }
}
