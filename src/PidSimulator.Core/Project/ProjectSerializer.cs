using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using PidSimulator.Core.Models;
using PidSimulator.Core.Plc;

namespace PidSimulator.Core.Project;

public static class ProjectSerializer
{
    public const string Extension = ".psim";
    public const string DemoFileName = "DEMO.psim";

    public static bool IsDemoFile(string? path) =>
        string.Equals(Path.GetFileName(path)?.TrimEnd(' ', '.'), DemoFileName, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string ToJson(ProjectDocument doc) => JsonSerializer.Serialize(
        doc.Snapshot(doc.Name, doc.Plc, doc.Data, doc.Targets), Options);

    public static ProjectDocument FromJson(string json)
    {
        using var source = JsonDocument.Parse(json);
        var current = ConvertToCurrentFormat(source.RootElement);
        var doc = current.Deserialize<ProjectDocument>(Options)
                  ?? throw new InvalidDataException("プロジェクトファイルが空です。");
        doc.Data.CsvFolder = DataSettings.PortableCsvFolder(doc.Data.CsvFolder);
        if (doc.Data.TrendMinutes is < TrendBuffer.MinRetentionMinutes or > TrendBuffer.MaxRetentionMinutes)
            throw new InvalidDataException($"トレンド保持時間は {TrendBuffer.MinRetentionMinutes}～{TrendBuffer.MaxRetentionMinutes} 分で指定してください。");
        foreach (var t in doc.Targets)
        {
            if (!Enum.IsDefined(t.Kind)) throw new InvalidDataException($"「{t.Name}」のモデル種類が不明です。");
            if (t.Params is null) throw new InvalidDataException($"「{t.Name}」のモデルパラメータがありません。");
            if (!ModelCatalog.ValidateParameters(t.Kind, t.Params, out var error))
                throw new InvalidDataException($"「{t.Name}」: {error}");
            if (!EngineeringUnits.IsSupportedMvUnit(t.Kind, t.MvRange.Unit, t.MvOnOff))
                throw new InvalidDataException($"「{t.Name}」: このモデル・制御方式のMV単位は {string.Join("・", EngineeringUnits.GetMvUnits(t.Kind, t.MvOnOff))} から選んでください。");
            if ((!t.MvOnOff && !PlcDataTypes.IsValidRange(t.DataType, t.MvRange))
                || !PlcDataTypes.IsValidRange(t.DataType, t.PvRange) || !PlcDataTypes.IsValidRange(t.DataType, t.SpRange))
                throw new InvalidDataException($"「{t.Name}」: データ型とRAWレンジを確認してください。");
            if (EngineeringUnits.IsLevel(t.Kind)
                && (t.PvRange.Unit is not ("%" or "mm") || t.SpRange.Unit != t.PvRange.Unit))
                throw new InvalidDataException($"「{t.Name}」: 液面のPV・SP単位は同じ % または mm にしてください。");
        }
        return doc;
    }

    private static JsonElement ConvertToCurrentFormat(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty(nameof(ProjectDocument.FormatVersion), out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int format))
            throw new JsonException("プロジェクトの形式番号（FormatVersion）が必要です。");
        if (format > ProjectDocument.CurrentFormat)
            throw new InvalidDataException("新しい版のアプリで保存されたファイルです。アプリを更新してから開いてください。");
        while (format < ProjectDocument.CurrentFormat)
        {
            document = ConvertOneVersion(document, format);
            format++;
        }
        return document;
    }

    // Published projects start at format 2; add each N -> N+1 conversion here when a new format is introduced.
    private static JsonElement ConvertOneVersion(JsonElement document, int format) =>
        throw new InvalidDataException($"プロジェクト形式 {format} には対応していません。このアプリで使用できる形式は {ProjectDocument.CurrentFormat} です。");

    /// <summary>一時ファイルに書いてから置き換える（書込み途中で壊れないように）</summary>
    public static void Save(string path, ProjectDocument doc)
    {
        if (IsDemoFile(path))
            throw new UnauthorizedAccessException("DEMO.psim は上書きできません。別の名前で保存してください。");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(doc), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static ProjectDocument Load(string path) => FromJson(File.ReadAllText(path, Encoding.UTF8));
}
