using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace xinread.Services;

/// <summary>
/// 下载并解码正文里的插图。图片来自微信读书的公共资源域，走 302 到带签名的 CDN 地址，
/// 不需要会话 cookie。解码后的位图按 URL 做 LRU 缓存（按像素估算占用，避免预取把内存撑爆），
/// 同一地址的并发请求共用一次下载，预取与正式显示之间不会重复拉取。
/// </summary>
internal static class RemoteImageLoader
{
    private const int MaxImageBytes = 16 * 1024 * 1024;

    /// <summary>位图缓存的内存预算：够放前后几章的插图，又不至于把整本书的图都留在内存里。</summary>
    private const long CacheBudgetBytes = 96L * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<CachedBitmap>> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<CachedBitmap> Recency = new();
    private static readonly Dictionary<string, Task<BitmapImage?>> InFlight = new(StringComparer.Ordinal);

    private static long _cachedBytes;

    /// <summary>把远程图片加载进 <paramref name="target"/>；返回是否成功，失败由调用方决定怎么收场。</summary>
    public static async Task<bool> ApplyAsync(Image target, string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        BitmapImage? bitmap = await GetBitmapAsync(url, cancellationToken);
        if (bitmap is null)
        {
            return false;
        }

        target.Source = bitmap;
        return true;
    }

    /// <summary>预热：只下载并缓存，不贴到任何控件上，翻到该章时可直接命中缓存。</summary>
    public static async Task<bool> PrefetchAsync(string url, CancellationToken cancellationToken)
        => await GetBitmapAsync(url, cancellationToken) is not null;

    /// <summary>取已解码的位图：命中缓存直接返回，否则下载并解码后入缓存。</summary>
    private static async Task<BitmapImage?> GetBitmapAsync(string url, CancellationToken cancellationToken)
    {
        // 部分书用协议相对地址（//res.weread...），按当前 HTTPS 语义补全。
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string key = uri.AbsoluteUri;
        try
        {
            // 预取和正式显示可能同时要同一张图：共用一次下载，谁先到谁发起。
            return await FetchAsync(key, uri).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or OperationCanceledException)
        {
            // 插图是附加内容，加载失败就由调用方折叠掉，不影响正文其余部分。
            return null;
        }
    }

    private static Task<BitmapImage?> FetchAsync(string key, Uri uri)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out LinkedListNode<CachedBitmap>? node))
            {
                Recency.Remove(node);
                Recency.AddFirst(node);
                return Task.FromResult<BitmapImage?>(node.Value.Bitmap);
            }

            if (InFlight.TryGetValue(key, out Task<BitmapImage?>? running))
            {
                return running;
            }

            Task<BitmapImage?> download = DownloadAsync(key, uri);
            InFlight[key] = download;
            return download;
        }
    }

    /// <summary>下载与解码只跑一次，且不受调用方令牌影响：预取被取消后，正式显示仍能复用这次结果。</summary>
    private static async Task<BitmapImage?> DownloadAsync(string key, Uri uri)
    {
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(uri);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                return null;
            }

            BitmapImage bitmap = new();
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }

                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
            }

            Remember(key, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException)
        {
            // 插图是附加内容，加载失败就由调用方折叠掉，不影响正文其余部分。
            return null;
        }
        finally
        {
            lock (Gate)
            {
                InFlight.Remove(key);
            }
        }
    }

    /// <summary>把解码好的位图放进 LRU 缓存，超出预算时按最久未用淘汰。</summary>
    private static void Remember(string key, BitmapImage bitmap)
    {
        long bytes = ApproximateBytes(bitmap);
        if (bytes > CacheBudgetBytes)
        {
            // 单张就超预算的大图不缓存，避免一张就把其他图挤干净。
            return;
        }

        lock (Gate)
        {
            if (Cache.ContainsKey(key))
            {
                return;
            }

            while (Cache.Count > 0 && _cachedBytes + bytes > CacheBudgetBytes)
            {
                LinkedListNode<CachedBitmap> oldest = Recency.Last!;
                Recency.RemoveLast();
                Cache.Remove(oldest.Value.Key);
                _cachedBytes -= oldest.Value.Bytes;
            }

            var node = new LinkedListNode<CachedBitmap>(new CachedBitmap(key, bitmap, bytes));
            Recency.AddFirst(node);
            Cache[key] = node;
            _cachedBytes += bytes;
        }
    }

    /// <summary>按 BGRA 估算解码后的内存占用，用作淘汰依据。</summary>
    private static long ApproximateBytes(BitmapImage bitmap)
        => bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
            ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4
            : 0;

    private sealed record CachedBitmap(string Key, BitmapImage Bitmap, long Bytes);
}
