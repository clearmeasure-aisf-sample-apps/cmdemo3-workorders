using System.Text.Json;
using ClearMeasure.Bootcamp.UI.Server.BuildFacts;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.BuildFacts;

[TestFixture]
public class BuildFactsDocumentTests
{
    private static readonly string[] Contract =
    [
        "version", "commit", "commitUrl", "builtAt", "buildUrl", "code", "tests", "coverage", "complexity", "crap",
        "analysis"
    ];

    [Test]
    public void Create_WhenNothingWasStamped_ShouldAnswerTheAssemblyVersionAndNulls()
    {
        using var document = JsonDocument.Parse(BuildFactsDocument.Create(null, "2.4.15+0123abcd"));

        var root = document.RootElement;
        PropertyNames(root).ShouldBe(Contract);
        root.GetProperty("version").GetString().ShouldBe("2.4.15");
        foreach (var name in Contract.Skip(1))
        {
            root.GetProperty(name).ValueKind.ShouldBe(JsonValueKind.Null, name);
        }
    }

    [TestCase("2.4.15", "2.4.15")]
    [TestCase("1.0.0+b9e418ad5084086cad17a6a64e7b71a11ecc59c8", "1.0.0")]
    [TestCase("3.0.0-rc.1+sha", "3.0.0-rc.1")]
    public void Create_WhenNothingWasStamped_ShouldDropTheSourceRevisionFromTheVersion(
        string informationalVersion,
        string expected)
    {
        using var document = JsonDocument.Parse(BuildFactsDocument.Create(null, informationalVersion));

        document.RootElement.GetProperty("version").GetString().ShouldBe(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void Create_WhenTheAssemblyHasNoVersion_ShouldAnswerNull(string? informationalVersion)
    {
        using var document = JsonDocument.Parse(BuildFactsDocument.Create(null, informationalVersion));

        document.RootElement.GetProperty("version").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public void Create_WhenFactsWereStamped_ShouldAnswerThemInContractOrder()
    {
        var stamped = BuildFactsDocument.Parse(
            """
            {
              "analysis": { "qodanaProblems": 0 },
              "crap": { "max": 5.8, "threshold": 6, "overThreshold": 0 },
              "complexity": { "average": 1.9, "max": 34, "methods": 5210 },
              "coverage": { "linePercent": 81.2, "branchPercent": 70.1 },
              "tests": { "unit": 1009, "integration": 240, "acceptance": 168 },
              "code": {
                "linesOfCode": 84210,
                "files": 1203,
                "languages": [ { "name": "C#", "lines": 61234, "files": 800 } ]
              },
              "buildUrl": "https://github.com/org/repo/actions/runs/42",
              "builtAt": "2026-10-06T05:00:00Z",
              "commitUrl": "https://github.com/org/repo/commit/0123abcd",
              "commit": "0123abcd",
              "version": "2.4.15"
            }
            """);

        using var document = JsonDocument.Parse(BuildFactsDocument.Create(stamped, "1.0.0+local"));

        var root = document.RootElement;
        PropertyNames(root).ShouldBe(Contract);
        root.GetProperty("version").GetString().ShouldBe("2.4.15");
        root.GetProperty("commit").GetString().ShouldBe("0123abcd");
        root.GetProperty("commitUrl").GetString().ShouldBe("https://github.com/org/repo/commit/0123abcd");
        root.GetProperty("builtAt").GetString().ShouldBe("2026-10-06T05:00:00Z");
        root.GetProperty("buildUrl").GetString().ShouldBe("https://github.com/org/repo/actions/runs/42");
        root.GetProperty("code").GetProperty("linesOfCode").GetInt32().ShouldBe(84210);
        root.GetProperty("code").GetProperty("languages")[0].GetProperty("name").GetString().ShouldBe("C#");
        root.GetProperty("tests").GetProperty("acceptance").GetInt32().ShouldBe(168);
        root.GetProperty("coverage").GetProperty("linePercent").GetDouble().ShouldBe(81.2);
        root.GetProperty("complexity").GetProperty("max").GetInt32().ShouldBe(34);
        root.GetProperty("crap").GetProperty("threshold").GetInt32().ShouldBe(6);
        root.GetProperty("analysis").GetProperty("qodanaProblems").GetInt32().ShouldBe(0);
    }

    [Test]
    public void Create_WhenSectionsAreMissingOrNull_ShouldAnswerNullForThemAndTheAssemblyVersion()
    {
        var stamped = BuildFactsDocument.Parse("""{ "commit": "0123abcd", "version": null, "coverage": null }""");

        using var document = JsonDocument.Parse(BuildFactsDocument.Create(stamped, "2.4.15+0123abcd"));

        var root = document.RootElement;
        PropertyNames(root).ShouldBe(Contract);
        root.GetProperty("version").GetString().ShouldBe("2.4.15");
        root.GetProperty("commit").GetString().ShouldBe("0123abcd");
        root.GetProperty("coverage").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("tests").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public void Create_WhenTheStampedFileHasMoreProperties_ShouldKeepThemAfterTheContract()
    {
        var stamped = BuildFactsDocument.Parse("""{ "pipeline": { "runner": "ubuntu" }, "version": "2.4.15" }""");

        using var document = JsonDocument.Parse(BuildFactsDocument.Create(stamped, null));

        var root = document.RootElement;
        PropertyNames(root).ShouldBe([.. Contract, "pipeline"]);
        root.GetProperty("pipeline").GetProperty("runner").GetString().ShouldBe("ubuntu");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{ \"version\": ")]
    [TestCase("[1, 2, 3]")]
    [TestCase("\"2.4.15\"")]
    [TestCase("null")]
    public void Parse_WhenTheContentIsNotAJsonObject_ShouldReturnNull(string? content)
    {
        BuildFactsDocument.Parse(content).ShouldBeNull();
    }

    private static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name)];
}
