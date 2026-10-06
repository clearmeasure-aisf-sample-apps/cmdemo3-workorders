using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.BuildGates;

/// <summary>
/// The Release workflow, the script it calls and the Build workflow's artifacts have to agree, or
/// <c>GET /_build</c> answers nulls without anything failing.
/// </summary>
[TestFixture]
public class BuildFactsWorkflowTests
{
    private const string StampStep = "name: Stamp the build facts into the image's files";

    [Test]
    public void ReleaseWorkflow_WhenRead_StampsTheFactsBetweenExtractingTheUiAndBuildingTheImage()
    {
        var yaml = File.ReadAllText(FindRepoFile(Path.Combine(".github", "workflows", "release.yml")));

        var extract = yaml.IndexOf("name: Extract the UI for the image", StringComparison.Ordinal);
        var stamp = yaml.IndexOf(StampStep, StringComparison.Ordinal);
        var image = yaml.IndexOf("name: Build and push the image", StringComparison.Ordinal);

        extract.ShouldBeGreaterThan(-1);
        stamp.ShouldBeGreaterThan(extract);
        image.ShouldBeGreaterThan(stamp);
    }

    [Test]
    public void ReleaseWorkflow_WhenRead_CallsTheScriptWithParametersItHas()
    {
        var yaml = File.ReadAllText(FindRepoFile(Path.Combine(".github", "workflows", "release.yml")));
        var script = File.ReadAllText(FindRepoFile(Path.Combine("scripts", "Write-BuildFacts.ps1")));
        var start = yaml.IndexOf(StampStep, StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1);
        var step = yaml.Substring(start, yaml.IndexOf("\n      - ", start, StringComparison.Ordinal) - start);

        step.ShouldContain("if: hashFiles('scripts/Write-BuildFacts.ps1') != ''");
        step.ShouldContain("GH_TOKEN: ${{ github.token }}");
        step.ShouldContain("./scripts/Write-BuildFacts.ps1 -DownloadArtifacts");
        step.ShouldContain("-RunId '${{ github.event.workflow_run.id }}'");
        // The image is built from ./built (Dockerfile: COPY /built/ /app): the file is written there, not into a zip.
        step.ShouldContain("-OutputPath 'built/build-facts.json'");
        step.ShouldNotContain("-Package");
        foreach (var parameter in new[] { "DownloadArtifacts", "Version", "RunId", "BuiltAt", "OutputPath" })
        {
            step.ShouldContain($"-{parameter}");
            script.ShouldMatch($@"\[(string|switch)\]\${parameter}\b");
        }
    }

    [TestCase("test-results-linux")]
    [TestCase("test-results-acceptance")]
    [TestCase("code-coverage-linux")]
    [TestCase("crap-metrics-linux")]
    [TestCase("qodana-report")]
    public void BuildWorkflow_WhenRead_UploadsTheArtifactTheScriptReads(string artifact)
    {
        var yaml = File.ReadAllText(FindRepoFile(Path.Combine(".github", "workflows", "build.yml")));
        var script = File.ReadAllText(FindRepoFile(Path.Combine("scripts", "Write-BuildFacts.ps1")));

        yaml.ShouldContain($"name: {artifact}");
        script.ShouldContain($"'{artifact}'");
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"{relativePath} not found from test directory.");
    }
}
