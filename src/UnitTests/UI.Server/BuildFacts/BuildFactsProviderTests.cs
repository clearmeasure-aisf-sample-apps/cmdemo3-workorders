using System.Text.Json;
using ClearMeasure.Bootcamp.UI.Server.BuildFacts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClearMeasure.Bootcamp.UnitTests.UI.Server.BuildFacts;

[TestFixture]
public class BuildFactsProviderTests
{
    private string _contentRoot = null!;

    [SetUp]
    public void CreateContentRoot()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), $"build-facts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_contentRoot);
    }

    [TearDown]
    public void DeleteContentRoot()
    {
        Directory.Delete(_contentRoot, true);
    }

    [Test]
    public void Json_WhenTheContentRootHasNoFile_ShouldAnswerTheAssemblyVersionAndNulls()
    {
        var provider = new BuildFactsProvider(_contentRoot, "1.0.0+local", NullLogger.Instance);

        using var document = JsonDocument.Parse(provider.Json);

        document.RootElement.GetProperty("version").GetString().ShouldBe("1.0.0");
        document.RootElement.GetProperty("commit").ValueKind.ShouldBe(JsonValueKind.Null);
        document.RootElement.GetProperty("code").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public void Json_WhenTheReleaseStampedTheFile_ShouldAnswerItsFacts()
    {
        File.WriteAllText(
            Path.Combine(_contentRoot, BuildFactsProvider.FileName),
            """{ "version": "2.4.15", "commit": "0123abcd", "tests": { "unit": 1009 } }""");
        var provider = new BuildFactsProvider(_contentRoot, "1.0.0+local", NullLogger.Instance);

        using var document = JsonDocument.Parse(provider.Json);

        document.RootElement.GetProperty("version").GetString().ShouldBe("2.4.15");
        document.RootElement.GetProperty("commit").GetString().ShouldBe("0123abcd");
        document.RootElement.GetProperty("tests").GetProperty("unit").GetInt32().ShouldBe(1009);
        document.RootElement.GetProperty("coverage").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public void Json_WhenReadTwice_ShouldReadTheFileOnce()
    {
        var path = Path.Combine(_contentRoot, BuildFactsProvider.FileName);
        File.WriteAllText(path, """{ "version": "2.4.15" }""");
        var provider = new BuildFactsProvider(_contentRoot, null, NullLogger.Instance);
        var first = provider.Json;

        File.WriteAllText(path, """{ "version": "9.9.9" }""");

        provider.Json.ShouldBeSameAs(first);
    }

    [Test]
    public void Json_WhenTheFileIsNotAJsonObject_ShouldAnswerWithoutItAndWarn()
    {
        File.WriteAllText(Path.Combine(_contentRoot, BuildFactsProvider.FileName), "{ \"version\": ");
        var logger = new StubLogger();
        var provider = new BuildFactsProvider(_contentRoot, "1.0.0+local", logger);

        using var document = JsonDocument.Parse(provider.Json);

        document.RootElement.GetProperty("version").GetString().ShouldBe("1.0.0");
        logger.Warnings.ShouldHaveSingleItem().ShouldContain(BuildFactsProvider.FileName);
    }

    [Test]
    public void Json_WhenThereIsNoFile_ShouldNotWarn()
    {
        var logger = new StubLogger();
        var provider = new BuildFactsProvider(_contentRoot, "1.0.0+local", logger);

        provider.Json.ShouldNotBeNullOrEmpty();

        logger.Warnings.ShouldBeEmpty();
    }

    private sealed class StubLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
