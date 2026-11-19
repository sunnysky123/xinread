using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace xinread.Services;

/// <summary>阅读进度的序列化上下文，保证 Trim 发布后仍可工作。</summary>
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class PositionJsonContext : JsonSerializerContext;

/// <summary>按书记住上次读到的章节，用于打开书时继续阅读。</summary>
public sealed class ReadingPositionStore
{
    private readonly string _path;
    private Dictionary<string, long>? _positions;

    public ReadingPositionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xinread",
            "positions.json");
    }

    public long? GetChapter(string bookId)
    {
        Dictionary<string, long> positions = Load();
        return positions.TryGetValue(bookId, out long chapterUid) ? chapterUid : null;
    }

    public async Task SetChapterAsync(string bookId, long chapterUid)
    {
        Dictionary<string, long> positions = Load();
        positions[bookId] = chapterUid;

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(positions, PositionJsonContext.Default.DictionaryStringInt64))
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 阅读进度是尽力而为的附加功能，写失败不影响阅读。
        }
    }

    private Dictionary<string, long> Load()
    {
        if (_positions is not null)
        {
            return _positions;
        }

        try
        {
            if (File.Exists(_path))
            {
                using FileStream stream = File.OpenRead(_path);
                _positions = JsonSerializer
                    .Deserialize(stream, PositionJsonContext.Default.DictionaryStringInt64);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _positions = null;
        }

        return _positions ??= [];
    }
}
