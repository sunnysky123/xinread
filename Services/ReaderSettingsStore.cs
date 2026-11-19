using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace xinread.Services;

/// <summary>阅读器设置的序列化上下文，保证 Trim 发布后仍可工作。</summary>
[JsonSerializable(typeof(ReaderSettings))]
internal sealed partial class ReaderSettingsJsonContext : JsonSerializerContext;

/// <summary>大声朗读的参数，全部为 0 / null 时由阅读页回退到默认值。</summary>
public sealed record SpeechOptions
{
    /// <summary>语速，0.5–3.0。</summary>
    public double Rate { get; init; }

    /// <summary>音量，0.1–1.0。</summary>
    public double Volume { get; init; }

    /// <summary>语音 Id（<c>VoiceInformation.Id</c>），null 表示使用系统默认语音。</summary>
    public string? Voice { get; init; }

    /// <summary>读完当前章节后自动翻到下一章接着读。</summary>
    public bool AutoNextChapter { get; init; }
}

/// <summary>需要跟着用户走的阅读器设置。</summary>
public sealed class ReaderSettings
{
    /// <summary>正文字号（px）。</summary>
    public int FontSize { get; set; }

    /// <summary>大声朗读参数。</summary>
    public SpeechOptions Speech { get; set; } = new();
}

/// <summary>
/// 按本机用户保存阅读器外观设置。未打包应用拿不到 <c>ApplicationData.Current</c>，
/// 因此与阅读进度一样落在 LocalApplicationData 下的 JSON 文件里。
/// </summary>
public sealed class ReaderSettingsStore
{
    private readonly string _path;
    private ReaderSettings? _settings;

    public ReaderSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xinread",
            "readersettings.json");
    }

    /// <summary>字号，0 表示用户还没调过，由调用方决定默认值。</summary>
    public int FontSize
    {
        get => Load().FontSize;
        set
        {
            ReaderSettings settings = Load();
            if (settings.FontSize == value)
            {
                return;
            }

            settings.FontSize = value;
            Save(settings);
        }
    }

    /// <summary>朗读参数，整体替换以便一次写盘。</summary>
    public SpeechOptions Speech
    {
        get => Load().Speech;
        set
        {
            ReaderSettings settings = Load();
            if (settings.Speech == value)
            {
                return;
            }

            settings.Speech = value;
            Save(settings);
        }
    }

    private ReaderSettings Load()
    {
        if (_settings is not null)
        {
            return _settings;
        }

        try
        {
            if (File.Exists(_path))
            {
                using FileStream stream = File.OpenRead(_path);
                _settings = JsonSerializer
                    .Deserialize(stream, ReaderSettingsJsonContext.Default.ReaderSettings);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _settings = null;
        }

        _settings ??= new ReaderSettings();
        // 手改过的存档可能写着 "Speech": null，兜一层避免后面空引用。
        _settings.Speech ??= new SpeechOptions();
        return _settings;
    }

    private void Save(ReaderSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(settings, ReaderSettingsJsonContext.Default.ReaderSettings));
        }
        catch (IOException)
        {
            // 外观设置是尽力而为的附加功能，写失败不影响阅读。
        }
    }
}
