using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Shouldly;

namespace ClearMeasure.Bootcamp.IntegrationTests.BuildGates;

/// <summary>
/// Runs <c>scripts/Write-BuildFacts.ps1</c> against a small fabricated repository and fabricated Build artifacts
/// (the layout <c>.github/workflows/build.yml</c> uploads), and reads the file it writes.
/// </summary>
[TestFixture]
public class BuildFactsScriptTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private string _workDirectory = null!;
    private string _repository = null!;
    private string _artifacts = null!;
    private JsonDocument _facts = null!;

    [OneTimeSetUp]
    public void WriteTheFactsOfAFabricatedBuild()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), $"build-facts-{Guid.NewGuid():N}");
        _repository = Path.Combine(_workDirectory, "repository");
        _artifacts = Path.Combine(_workDirectory, "artifacts");
        WriteRepository();
        WriteArtifacts(_artifacts);
        _facts = RunScript(_artifacts, out _);
    }

    [OneTimeTearDown]
    public void DeleteWorkDirectory()
    {
        _facts.Dispose();
        if (!Directory.Exists(_workDirectory))
        {
            return;
        }

        // Git marks its object files read-only, which stops a recursive delete on Windows.
        foreach (var file in Directory.EnumerateFiles(_workDirectory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_workDirectory, true);
    }

    [Test]
    public void WriteBuildFacts_WhenEveryArtifactIsThere_WritesTheIdentityOfTheBuild()
    {
        var root = _facts.RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(
        [
            "version", "commit", "commitUrl", "builtAt", "buildUrl", "code", "tests", "coverage", "complexity",
            "crap", "analysis"
        ]);
        root.GetProperty("version").GetString().ShouldBe("2.4.15");
        root.GetProperty("commit").GetString().ShouldBe(Commit);
        root.GetProperty("commitUrl").GetString().ShouldBe($"https://github.com/example-org/example-repo/commit/{Commit}");
        root.GetProperty("builtAt").GetString().ShouldBe("2026-10-06T05:00:00Z");
        root.GetProperty("buildUrl").GetString().ShouldBe("https://github.com/example-org/example-repo/actions/runs/424242");
    }

    [Test]
    public void WriteBuildFacts_WhenEveryArtifactIsThere_CountsTrackedSourceLinesPerLanguage()
    {
        var code = _facts.RootElement.GetProperty("code");
        code.GetProperty("linesOfCode").GetInt32().ShouldBe(13);
        code.GetProperty("files").GetInt32().ShouldBe(5);
        code.GetProperty("languages").EnumerateArray()
            .Select(language => (
                language.GetProperty("name").GetString(),
                language.GetProperty("lines").GetInt32(),
                language.GetProperty("files").GetInt32()))
            .ShouldBe(
            [
                ("C#", 7, 2),
                ("Markdown", 2, 1),
                ("Razor", 2, 1),
                ("SQL", 2, 1)
            ]);
    }

    [Test]
    public void WriteBuildFacts_WhenEveryArtifactIsThere_CountsEachKindOfTestOnce()
    {
        var tests = _facts.RootElement.GetProperty("tests");
        tests.GetProperty("unit").GetInt32().ShouldBe(4);
        tests.GetProperty("integration").GetInt32().ShouldBe(3);
        tests.GetProperty("acceptance").GetInt32().ShouldBe(2);
    }

    [Test]
    public void WriteBuildFacts_WhenEveryArtifactIsThere_MergesCoverageAndReadsComplexity()
    {
        var coverage = _facts.RootElement.GetProperty("coverage");
        coverage.GetProperty("linePercent").GetDouble().ShouldBe(75.0);
        coverage.GetProperty("branchPercent").GetDouble().ShouldBe(66.7);
        var complexity = _facts.RootElement.GetProperty("complexity");
        complexity.GetProperty("average").GetDouble().ShouldBe(4.0);
        complexity.GetProperty("max").GetInt32().ShouldBe(8);
        complexity.GetProperty("methods").GetInt32().ShouldBe(3);
    }

    [Test]
    public void WriteBuildFacts_WhenEveryArtifactIsThere_ReadsCrapOfProductionCodeAndQodanaProblems()
    {
        var crap = _facts.RootElement.GetProperty("crap");
        crap.GetProperty("max").GetDouble().ShouldBe(5.8);
        crap.GetProperty("threshold").GetInt32().ShouldBe(6);
        crap.GetProperty("overThreshold").GetInt32().ShouldBe(0);
        _facts.RootElement.GetProperty("analysis").GetProperty("qodanaProblems").GetInt32().ShouldBe(3);
    }

    [Test]
    public void WriteBuildFacts_WhenNoArtifactIsThere_WritesNullSectionsAndSucceeds()
    {
        var empty = Path.Combine(_workDirectory, "no-artifacts");
        Directory.CreateDirectory(empty);

        using var facts = RunScript(empty, out _);

        var root = facts.RootElement;
        root.GetProperty("version").GetString().ShouldBe("2.4.15");
        root.GetProperty("code").GetProperty("files").GetInt32().ShouldBe(5);
        foreach (var section in new[] { "tests", "coverage", "complexity", "crap", "analysis" })
        {
            root.GetProperty(section).ValueKind.ShouldBe(JsonValueKind.Null, section);
        }
    }

    [Test]
    public void WriteBuildFacts_WhenArtifactsAreUnreadable_WritesNullForThoseSectionsOnly()
    {
        var broken = Path.Combine(_workDirectory, "broken-artifacts");
        WriteArtifacts(broken);
        File.WriteAllText(Path.Combine(broken, "qodana-report", "qodana.sarif.json"), "{ \"runs\": [");
        File.WriteAllText(
            Path.Combine(broken, "code-coverage-linux", "UnitTests", "run", "In", "host", "coverage.cobertura.xml"),
            "<coverage><packages>");

        using var facts = RunScript(broken, out var log);

        var root = facts.RootElement;
        root.GetProperty("analysis").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("coverage").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("complexity").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("tests").GetProperty("unit").GetInt32().ShouldBe(4);
        root.GetProperty("crap").GetProperty("threshold").GetInt32().ShouldBe(6);
        log.ShouldContain("::warning title=Build facts::The section 'analysis' could not be read");
        log.ShouldContain("::warning title=Build facts::The section 'coverage reports' could not be read");
    }

    [Test]
    public void WriteBuildFacts_WhenTheQodanaReportIsAZipInTheArtifact_ReadsTheSarifInsideIt()
    {
        // What the Qodana action uploads: one qodana-report.zip with its results directory.
        var artifacts = Path.Combine(_workDirectory, "zipped-qodana-artifacts");
        WriteArtifacts(artifacts);
        var report = Path.Combine(artifacts, "qodana-report");
        File.Delete(Path.Combine(report, "qodana.sarif.json"));
        using (var archive = ZipFile.Open(Path.Combine(report, "qodana-report.zip"), ZipArchiveMode.Create))
        {
            WriteEntry(archive, "log/idea.log", "started");
            WriteEntry(archive, "qodana-short.sarif.json", Sarif("new"));
            WriteEntry(archive, "report/index.html", "<html></html>");
            WriteEntry(archive, "report/results/qodana.sarif.json", Sarif("new", "new"));
            WriteEntry(archive, "qodana.sarif.json", Sarif("unchanged", "new", "absent", "unchanged", "unchanged", "new"));
        }

        using var facts = RunScript(artifacts, out var log);

        facts.RootElement.GetProperty("analysis").GetProperty("qodanaProblems").GetInt32().ShouldBe(5);
        log.ShouldContain("PASS analysis");
        Directory.GetFileSystemEntries(report).Select(Path.GetFileName).ShouldBe(["qodana-report.zip"]);
    }

    [Test]
    public void WriteBuildFacts_WhenTheZipInTheArtifactHasNoSarif_WritesNullForAnalysis()
    {
        var artifacts = Path.Combine(_workDirectory, "zip-without-sarif-artifacts");
        WriteArtifacts(artifacts);
        var report = Path.Combine(artifacts, "qodana-report");
        File.Delete(Path.Combine(report, "qodana.sarif.json"));
        using (var archive = ZipFile.Open(Path.Combine(report, "qodana-report.zip"), ZipArchiveMode.Create))
        {
            WriteEntry(archive, "qodana-short.sarif.json", Sarif("new"));
        }

        using var facts = RunScript(artifacts, out var log);

        facts.RootElement.GetProperty("analysis").ValueKind.ShouldBe(JsonValueKind.Null);
        facts.RootElement.GetProperty("tests").GetProperty("unit").GetInt32().ShouldBe(4);
        log.ShouldContain("SKIP analysis: no input");
    }

    [Test]
    public void WriteBuildFacts_WhenTheZipInTheArtifactIsUnreadable_WritesNullForAnalysisAndWarns()
    {
        var artifacts = Path.Combine(_workDirectory, "broken-zip-artifacts");
        WriteArtifacts(artifacts);
        var report = Path.Combine(artifacts, "qodana-report");
        File.Delete(Path.Combine(report, "qodana.sarif.json"));
        File.WriteAllText(Path.Combine(report, "qodana-report.zip"), "not a zip");

        using var facts = RunScript(artifacts, out var log);

        facts.RootElement.GetProperty("analysis").ValueKind.ShouldBe(JsonValueKind.Null);
        facts.RootElement.GetProperty("crap").GetProperty("threshold").GetInt32().ShouldBe(6);
        log.ShouldContain("::warning title=Build facts::The section 'analysis' could not be read");
    }

    [Test]
    public void WriteBuildFacts_WhenGivenAPackage_PutsTheFileInItsRoot()
    {
        var package = Path.Combine(_workDirectory, "example-ui.2.4.15.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "ClearMeasure.Bootcamp.UI.Server.dll", "the app");
            WriteEntry(archive, "wwwroot/index.html", "<html></html>");
            WriteEntry(archive, "build-facts.json", "{ \"version\": \"stale\" }");
        }

        using var facts = RunScript(_artifacts, out _, "-Package", package);

        using var stamped = ZipFile.OpenRead(package);
        stamped.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ShouldBe(
        [
            "ClearMeasure.Bootcamp.UI.Server.dll", "build-facts.json", "wwwroot/index.html"
        ]);
        using var reader = new StreamReader(stamped.GetEntry("build-facts.json")!.Open());
        using var inPackage = JsonDocument.Parse(reader.ReadToEnd());
        inPackage.RootElement.GetProperty("version").GetString().ShouldBe("2.4.15");
        inPackage.RootElement.GetProperty("tests").GetProperty("unit").GetInt32()
            .ShouldBe(4);
        facts.RootElement.GetProperty("version").GetString().ShouldBe("2.4.15");
    }

    [Test]
    public void WriteBuildFacts_WhenThePackageIsMissing_Fails()
    {
        var output = Path.Combine(_workDirectory, $"facts-{Guid.NewGuid():N}.json");

        var exitCode = RunPwsh(
            out var log,
            "-OutputPath", output,
            "-RepoRoot", _repository,
            "-Commit", Commit,
            "-Package", Path.Combine(_workDirectory, "missing.zip"));

        exitCode.ShouldNotBe(0, log);
        log.ShouldContain("Package not found");
    }

    private JsonDocument RunScript(string artifacts, out string log, params string[] more)
    {
        var output = Path.Combine(_workDirectory, $"facts-{Guid.NewGuid():N}.json");
        string[] arguments =
        [
            "-OutputPath", output,
            "-RepoRoot", _repository,
            "-Version", "2.4.15",
            "-Commit", Commit,
            "-Repository", "example-org/example-repo",
            "-RunId", "424242",
            "-BuiltAt", "2026-10-06T05:00:00Z",
            "-ArtifactsPath", artifacts,
            .. more
        ];

        var exitCode = RunPwsh(out log, arguments);

        exitCode.ShouldBe(0, log);
        return JsonDocument.Parse(File.ReadAllText(output));
    }

    private static int RunPwsh(out string log, params string[] arguments)
    {
        var script = Path.Combine(FindRepoRoot(), "scripts", "Write-BuildFacts.ps1");
        File.Exists(script).ShouldBeTrue(script);
        return Run("pwsh", ["-NoProfile", "-File", script, .. arguments], Path.GetTempPath(), out log);
    }

    private static int Run(string fileName, string[] arguments, string workingDirectory, out string log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        process.ShouldNotBeNull();
        log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(120_000).ShouldBeTrue();
        return process.ExitCode;
    }

    private void WriteRepository()
    {
        WriteFile(_repository, "src/App/Program.cs", "namespace App;\n\npublic static class Program\n{\n    public static void Main() { }\n}\n");
        WriteFile(_repository, "src/App/Page.razor", "@page \"/\"\r\n\r\n<h1>Home</h1>\r\n");
        WriteFile(_repository, "src/Database/001_Create.sql", "CREATE TABLE dbo.WorkOrder\n\t(Id INT)\n   \n");
        WriteFile(_repository, "README.md", "# Example\n\nText.\n");
        WriteFile(_repository, "src/App/Service.cs", "namespace App;\npublic class Service;\n");

        // Tracked, but not written by hand or not a language that is counted.
        WriteFile(_repository, "src/App/Generated/Client.cs", "public class Client;\n");
        WriteFile(_repository, "src/App/Model.Designer.cs", "public class Model;\n");
        WriteFile(_repository, "src/App/wwwroot/lib/vendor/vendor.js", "var a = 1;\n");
        WriteFile(_repository, "src/App/wwwroot/css/site.min.css", "a{b:c}\n");
        WriteFile(_repository, "src/App/bin/Release/Leftover.cs", "public class Leftover;\n");
        WriteFile(_repository, "src/App/appsettings.json", "{ \"a\": 1 }\n");

        RunGit("init", "--quiet");
        RunGit("add", "--all");

        // In the working copy, but not tracked.
        WriteFile(_repository, "src/App/Scratch.cs", "public class Scratch;\n");
    }

    private void RunGit(params string[] arguments)
    {
        Run("git", arguments, _repository, out var log).ShouldBe(0, log);
    }

    private static void WriteArtifacts(string artifacts)
    {
        // upload-artifact keeps the path below the common parent of what it uploads: build/test for the unit and
        // integration results, the folder of the single trx file for the acceptance results.
        WriteFile(artifacts, "test-results-linux/UnitTests/unit.trx", Trx("ClearMeasure.Bootcamp.UnitTests.dll", 5, 4));
        WriteFile(
            artifacts,
            "test-results-linux/IntegrationTests/integration.trx",
            Trx("ClearMeasure.Bootcamp.IntegrationTests.dll", 3, 3));
        WriteFile(artifacts, "test-results-acceptance/acceptance.trx", Trx("ClearMeasure.Bootcamp.AcceptanceTests.dll", 2, 2));

        // The same unit tests on another platform: not counted again.
        WriteFile(artifacts, "test-results-sqlite/UnitTests/unit.trx", Trx("ClearMeasure.Bootcamp.UnitTests.dll", 99, 99));

        WriteFile(
            artifacts,
            "code-coverage-linux/UnitTests/run/In/host/coverage.cobertura.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.5" branch-rate="0.1666" version="1.9" timestamp="1" lines-covered="2" lines-valid="4" branches-covered="1" branches-valid="6">
              <sources><source>/repo/src/</source></sources>
              <packages>
                <package name="App" line-rate="0.5" branch-rate="0.1666" complexity="4">
                  <classes>
                    <class name="App.Service" filename="App/Service.cs" line-rate="0.5" branch-rate="0.1666" complexity="4">
                      <methods>
                        <method name="Run" signature="()" line-rate="0.5" branch-rate="0.1666" complexity="3">
                          <lines>
                            <line number="1" hits="1" branch="False" />
                            <line number="2" hits="1" branch="True" condition-coverage="50% (1/2)" />
                            <line number="3" hits="0" branch="True" condition-coverage="0% (0/4)" />
                          </lines>
                        </method>
                        <method name="get_Name" signature="()" line-rate="0" branch-rate="1" complexity="1">
                          <lines><line number="4" hits="0" branch="False" /></lines>
                        </method>
                      </methods>
                      <lines>
                        <line number="1" hits="1" branch="False" />
                        <line number="2" hits="1" branch="True" condition-coverage="50% (1/2)" />
                        <line number="3" hits="0" branch="True" condition-coverage="0% (0/4)" />
                        <line number="4" hits="0" branch="False" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        WriteFile(
            artifacts,
            "code-coverage-linux/IntegrationTests/run/In/host/coverage.cobertura.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.5" branch-rate="0.6666" version="1.9" timestamp="1" lines-covered="2" lines-valid="4" branches-covered="4" branches-valid="6">
              <sources><source>/repo/src/</source></sources>
              <packages>
                <package name="App" line-rate="0.5" branch-rate="0.6666" complexity="12">
                  <classes>
                    <class name="App.Service" filename="App/Service.cs" line-rate="0.5" branch-rate="0.6666" complexity="4">
                      <methods>
                        <method name="Run" signature="()" line-rate="0.6666" branch-rate="0.6666" complexity="3">
                          <lines />
                        </method>
                        <method name="get_Name" signature="()" line-rate="0" branch-rate="1" complexity="1">
                          <lines />
                        </method>
                      </methods>
                      <lines>
                        <line number="1" hits="0" branch="False" />
                        <line number="2" hits="7" branch="True" condition-coverage="100% (2/2)" />
                        <line number="3" hits="7" branch="True" condition-coverage="50% (2/4)" />
                        <line number="4" hits="0" branch="False" />
                      </lines>
                    </class>
                    <class name="App.Handler" filename="App/Handler.cs" line-rate="1" branch-rate="1" complexity="8">
                      <methods>
                        <method name="Handle" signature="(System.String)" line-rate="1" branch-rate="1" complexity="8">
                          <lines />
                        </method>
                      </methods>
                      <lines />
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        // rollup-file-scores.csx: the wrapper is camelCase, the file scores are PascalCase.
        WriteFile(
            artifacts,
            "crap-metrics-linux/crap-by-file.json",
            """
            {
              "schemaVersion": "1.0",
              "threshold": 6,
              "fileCount": 3,
              "productionFileCount": 2,
              "files": [
                { "FilePath": "/repo/src/UnitTests/ServiceTests.cs", "MethodCount": 9, "CrappyMethodCount": 3, "MaxCrap": 42.0, "IsProduction": false },
                { "FilePath": "/repo/src/App/Service.cs", "MethodCount": 2, "CrappyMethodCount": 0, "MaxCrap": 5.8312, "IsProduction": true },
                { "FilePath": "/repo/src/App/Handler.cs", "MethodCount": 1, "CrappyMethodCount": 0, "MaxCrap": 2, "IsProduction": true }
              ]
            }
            """);
        WriteFile(
            artifacts,
            "crap-metrics-linux/crap-production-violations.json",
            """{ "schemaVersion": "1.0", "threshold": 6, "violationCount": 0, "methods": [] }""");

        WriteFile(artifacts, "qodana-report/qodana.sarif.json", Sarif("unchanged", "unchanged", "new", "absent"));
    }

    // One result per baseline state, in the shape Qodana writes when it compares with a baseline.
    private static string Sarif(params string[] baselineStates)
    {
        var results = baselineStates.Select(state =>
            $$"""{ "ruleId": "ConvertToPrimaryConstructor", "baselineState": "{{state}}" }""");
        return $$"""{ "version": "2.1.0", "runs": [ { "results": [ {{string.Join(", ", results)}} ] } ] }""";
    }

    private static string Trx(string assembly, int total, int executed) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <TestRun id="5d0f3e2a-1111-4222-8333-444455556666" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
           <TestDefinitions>
             <UnitTest name="Should_Work" storage="/repo/src/tests/bin/release/net10.0/{assembly.ToLowerInvariant()}" id="6f1c7c8e-1111-4222-8333-444455556666">
               <TestMethod codeBase="/repo/src/Tests/bin/Release/net10.0/{assembly}" adapterTypeName="executor://nunit3testexecutor/" className="Example.Tests" name="Should_Work" />
             </UnitTest>
           </TestDefinitions>
           <ResultSummary outcome="Completed">
             <Counters total="{total}" executed="{executed}" passed="{executed}" failed="0" error="0" notExecuted="{total - executed}" />
           </ResultSummary>
         </TestRun>
         """;

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "ChurchBulletin.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test directory.");
    }
}
