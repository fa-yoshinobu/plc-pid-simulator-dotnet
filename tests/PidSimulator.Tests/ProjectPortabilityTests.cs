using System.Text.Json;
using System.Text.Json.Nodes;
using PidSimulator.Core;
using PidSimulator.Core.Models;
using PidSimulator.Core.Project;

namespace PidSimulator.Tests;

public class ProjectPortabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultCsvFolderIsPortableWithoutMutatingTheDocument(bool legacyAbsoluteDefault)
    {
        var document = new ProjectDocument();
        if (legacyAbsoluteDefault) document.Data.CsvFolder = DataSettings.DefaultCsvFolder;
        string original = document.Data.CsvFolder;

        string json = ProjectSerializer.ToJson(document);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("", parsed.RootElement.GetProperty("Data").GetProperty("CsvFolder").GetString());
        Assert.Equal(original, document.Data.CsvFolder);
        var loaded = ProjectSerializer.FromJson(json);
        Assert.Equal("", loaded.Data.CsvFolder);

        // Simulate two accounts' Documents roots without changing the current Windows profile.
        string firstUser = Path.Combine(Path.GetTempPath(), "first-user-documents");
        string secondUser = Path.Combine(Path.GetTempPath(), "second-user-documents");
        Assert.Equal(Path.Combine(firstUser, "PidSimulator", "records"), loaded.Data.GetCsvFolder(firstUser));
        Assert.Equal(Path.Combine(secondUser, "PidSimulator", "records"), loaded.Data.GetCsvFolder(secondUser));
    }

    [Fact]
    public void OldFormatTwoAbsoluteDefaultIsNormalizedWhenLoaded()
    {
        var json = JsonNode.Parse(ProjectSerializer.ToJson(new ProjectDocument()))!;
        json["Data"]!["CsvFolder"] = DataSettings.DefaultCsvFolder;
        Assert.Equal("", ProjectSerializer.FromJson(json.ToJsonString()).Data.CsvFolder);
    }

    [Fact]
    public void ExplicitCsvFolderIsPreservedAcrossUsers()
    {
        string path = Path.Combine(Path.GetTempPath(), "explicit-records");
        var document = new ProjectDocument { Data = new DataSettings { CsvFolder = path } };
        var loaded = ProjectSerializer.FromJson(ProjectSerializer.ToJson(document));
        Assert.Equal(path, loaded.Data.CsvFolder);
        Assert.Equal(path, loaded.Data.GetCsvFolder(Path.Combine(Path.GetTempPath(), "other-user")));
    }

    [Fact]
    public void FutureFormatIsExplainedBeforeParsingFutureFields()
    {
        const string json = """{"FormatVersion":3,"Plc":{"Mode":"FutureProtocol"}}""";
        var error = Assert.Throws<InvalidDataException>(() => ProjectSerializer.FromJson(json));
        Assert.Contains("新しい版のアプリで保存されたファイル", error.Message);
        Assert.Contains("更新", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"FormatVersion\":\"2\"}")]
    public void FormatNumberMustBePresentAndNumeric(string json) =>
        Assert.Throws<JsonException>(() => ProjectSerializer.FromJson(json));

    [Fact]
    public void RuntimeTargetAndProjectSnapshotPreserveUnknownFieldsWhenEditedAndSaved()
    {
        var config = TargetConfig.Default(ModelKind.Level);
        config.Name = "original";
        config.MvAddress = "D1000";
        config.PvAddress = "D1001";
        config.SpAddress = "D1002";
        var node = JsonNode.Parse(ProjectSerializer.ToJson(new ProjectDocument { Targets = [config] }))!;
        node["FutureRoot"] = JsonNode.Parse("""{"array":[true,null,3],"text":"将来の設定"}""");
        node["Plc"]!["FuturePlc"] = "connection option";
        node["Data"]!["FutureData"] = 42;
        node["Targets"]![0]!["FutureTarget"] = JsonNode.Parse("""{"enabled":true}""");
        node["Targets"]![0]!["MvRange"]!["FutureRange"] = "mv";
        node["Targets"]![0]!["PvRange"]!["FutureRange"] = "pv";
        node["Targets"]![0]!["SpRange"]!["FutureRange"] = "sp";
        var loaded = ProjectSerializer.FromJson(node.ToJsonString());

        var target = ControlTarget.FromConfig(loaded.Targets[0]);
        var edited = target.ToConfig();
        edited.Name = "edited";
        target.ApplySettings(edited);
        var currentData = loaded.Data.Clone();
        currentData.TrendMinutes = 60;
        var snapshot = loaded.Snapshot("saved-as", loaded.Plc.Clone(), currentData, [target.ToConfig()]);
        var saved = JsonNode.Parse(ProjectSerializer.ToJson(snapshot))!;

        Assert.True(JsonNode.DeepEquals(node["FutureRoot"], saved["FutureRoot"]));
        Assert.Equal("connection option", (string?)saved["Plc"]!["FuturePlc"]);
        Assert.Equal(42, (int?)saved["Data"]!["FutureData"]);
        Assert.Equal(60, (int?)saved["Data"]!["TrendMinutes"]);
        Assert.Equal("saved-as", (string?)saved["Name"]);
        Assert.Equal("edited", (string?)saved["Targets"]![0]!["Name"]);
        Assert.True(JsonNode.DeepEquals(node["Targets"]![0]!["FutureTarget"], saved["Targets"]![0]!["FutureTarget"]));
        foreach (string range in new[] { "MvRange", "PvRange", "SpRange" })
            Assert.True(JsonNode.DeepEquals(node["Targets"]![0]![range]!["FutureRange"], saved["Targets"]![0]![range]!["FutureRange"]));
        Assert.Equal("D1001", (string?)saved["Targets"]![0]!["PvAddress"]);
        Assert.Empty(loaded.Snapshot("deleted", loaded.Plc, loaded.Data, []).Targets);
    }

    [Fact]
    public void OnOffRangeUnknownFieldsSurviveRuntimeConversion()
    {
        var config = TargetConfig.Default(ModelKind.Level);
        config.MvOnOff = true;
        config.MvRange.ExtensionData = new() { ["FutureRange"] = JsonSerializer.SerializeToElement("kept") };
        Assert.Equal("kept", ControlTarget.FromConfig(config).ToConfig().MvRange.ExtensionData!["FutureRange"].GetString());
    }

    [Fact]
    public void CsvFolderProbeDetectsMissingAndNonDirectoryPathsWithoutLeavingFiles()
    {
        string directory = Directory.CreateTempSubdirectory("pid-csv-test-").FullName;
        try
        {
            Assert.True(DataSettings.CanWriteCsvFolder(directory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            Assert.False(DataSettings.CanWriteCsvFolder(Path.Combine(directory, "missing")));
            string file = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(file, "preserve");
            Assert.False(DataSettings.CanWriteCsvFolder(file));
            Assert.Equal("preserve", File.ReadAllText(file));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
