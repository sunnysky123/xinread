using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace xinread.Services;

/// <summary>系统语音，<see cref="Id"/> 是 <see cref="VoiceInformation.Id"/>，用于存档。</summary>
internal sealed record SpeechVoice(string Id, string Name, string Lang);

/// <summary>
/// 用系统自带的 <see cref="SpeechSynthesizer"/> 逐段朗读正文，替代原来的 WebView2
/// <c>speechSynthesis</c>。合成与播放串行进行：一段念完（MediaEnded）才合成下一段，
/// 既避免超长文本被引擎丢弃，也让合成开销不阻塞 UI 线程。
/// </summary>
internal sealed class SpeechReader : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpeechSynthesizer? _synth;
    private CancellationTokenSource? _run;
    private SpeechOptions _options = new();
    private TaskCompletionSource<bool>? _resume;
    private volatile bool _paused;
    private volatile bool _disposed;

    public SpeechReader()
    {
        // MediaPlayer 必须建在有 DispatcherQueue 的线程上，事件才会回到 UI 线程。
        _player = new MediaPlayer { AutoPlay = false };
        _player.CommandManager.IsEnabled = false;
    }

    /// <summary>开始朗读某一段，参数是段落在传入列表里的下标。</summary>
    public event Action<int>? ParagraphStarted;

    /// <summary>整章读完（不是被停止）。</summary>
    public event Action? Finished;

    /// <summary>合成或播放失败，参数是给用户看的错误信息。</summary>
    public event Action<string>? Failed;

    /// <summary>枚举系统已安装的语音，中文排在前面。</summary>
    public static IReadOnlyList<SpeechVoice> GetVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .Select(v => new SpeechVoice(v.Id, v.DisplayName, v.Language ?? string.Empty))
                .OrderByDescending(v => v.Lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                .ThenBy(v => v.Name, StringComparer.CurrentCulture)
                .ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>系统默认语音的名字，用于菜单里显示“跟随系统默认”的实际效果。</summary>
    public static string DefaultVoiceName()
    {
        try
        {
            return SpeechSynthesizer.DefaultVoice.DisplayName;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    public void Configure(SpeechOptions options) => _options = options ?? new SpeechOptions();

    /// <summary>从指定段落开始朗读，会先停掉正在进行的朗读。</summary>
    public void Start(IReadOnlyList<string> paragraphs, int startIndex)
    {
        Stop();
        if (paragraphs.Count == 0)
        {
            return;
        }

        var run = new CancellationTokenSource();
        _run = run;
        // RunAsync 负责在收尾时释放这个 CTS；这里传的是历史版本，不能被外部提前释放。
        _ = RunAsync(paragraphs, Math.Clamp(startIndex, 0, paragraphs.Count - 1), run);
    }

    /// <summary>
    /// 暂停朗读。段与段之间有一段合成空档（此时没有音频在播），暂停标志会保持住，
    /// 等合成完成后停在下一段开头，不会把后面的段落一直念下去。
    /// </summary>
    public bool Pause()
    {
        if (_run is null)
        {
            return false;
        }

        _paused = true;
        _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _player.Pause();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 源还没准备好时 Pause 可能报错，暂停标志已经置上，不影响后续。
        }

        return true;
    }

    /// <summary>清除暂停标志并从暂停位置继续。</summary>
    public bool Resume()
    {
        if (!_paused)
        {
            return false;
        }

        _paused = false;
        TaskCompletionSource<bool>? resume = Interlocked.Exchange(ref _resume, null);
        resume?.TrySetResult(true);

        try
        {
            _player.Play();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 同上：真正开始播放在 PlayAsync 里兜底。
        }

        return true;
    }

    /// <summary>停止朗读并丢弃后面的段落，不会触发 <see cref="Finished"/>。</summary>
    public void Stop()
    {
        CancellationTokenSource? run = Interlocked.Exchange(ref _run, null);

        try
        {
            run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 朗读任务已经收尾并释放了令牌，此时没有要取消的东西。
        }

        _paused = false;
        Interlocked.Exchange(ref _resume, null)?.TrySetResult(true);

        try
        {
            _player.Pause();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Player 已释放时忽略：停止是尽力而为的清理动作。
        }
    }

    private async Task RunAsync(IReadOnlyList<string> paragraphs, int startIndex, CancellationTokenSource run)
    {
        CancellationToken token = run.Token;
        try
        {
            for (int i = startIndex; i < paragraphs.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                string text = paragraphs[i];
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                SpeechSynthesisStream? stream = await SynthesizeAsync(text).ConfigureAwait(true);
                if (stream is null)
                {
                    continue;
                }

                if (token.IsCancellationRequested)
                {
                    stream.Dispose();
                    break;
                }

                // 合成期间被暂停：不要推进高亮，等恢复后再播这一段。
                await WaitWhilePausedAsync(token).ConfigureAwait(true);
                ParagraphStarted?.Invoke(i);
                await PlayAsync(stream, token).ConfigureAwait(true);
            }

            if (!token.IsCancellationRequested)
            {
                Finished?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // 用户停止或切章，属于正常流程。
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Failed?.Invoke(Describe(ex));
            }
        }
        finally
        {
            // RunAsync 是令牌唯一的释放者：Stop 只负责取消并摘掉引用，
            // 这样不会出现「用到一半被释放」或重复释放。
            Interlocked.CompareExchange(ref _run, null, run);
            run.Dispose();
        }
    }

    /// <summary>
    /// 合成放在线程池上：长段落要几百毫秒到数秒，会卡住 UI。
    /// <see cref="SpeechSynthesizer"/> 在多线程公寓下可用，全程只在池线程上创建和访问。
    /// </summary>
    private async Task<SpeechSynthesisStream?> SynthesizeAsync(string text)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            SpeechOptions options = _options;
            return await Task.Run(async () =>
            {
                SpeechSynthesizer synth = _synth ??= new SpeechSynthesizer();
                ApplyVoice(synth, options.Voice);
                // SSML 在 OneCore 中文语音上不可靠（SynthesizeSsmlToStreamAsync 抛 COMException），
                // 语速与音量只能走 Options；实测 AudioPitch 对中文语音不生效，故不提供。
                synth.Options.SpeakingRate = options.Rate;
                synth.Options.AudioVolume = options.Volume;
                return await synth.SynthesizeTextToStreamAsync(text);
            }).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ApplyVoice(SpeechSynthesizer synth, string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        VoiceInformation? voice = SpeechSynthesizer.AllVoices
            .FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));
        if (voice is not null && !string.Equals(synth.Voice?.Id, id, StringComparison.Ordinal))
        {
            synth.Voice = voice;
        }
    }

    /// <summary>播放一段并等它念完；被取消时提前返回。</summary>
    private async Task PlayAsync(SpeechSynthesisStream stream, CancellationToken token)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnEnded(MediaPlayer sender, object args) => done.TrySetResult(true);
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => done.TrySetResult(false);

        _player.MediaEnded += OnEnded;
        _player.MediaFailed += OnFailed;
        using CancellationTokenRegistration registration = token.Register(() => done.TrySetCanceled(token));

        try
        {
            await WaitWhilePausedAsync(token).ConfigureAwait(true);
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
            await done.Task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 停止朗读。
        }
        finally
        {
            _player.MediaEnded -= OnEnded;
            _player.MediaFailed -= OnFailed;

            try
            {
                _player.Pause();
                _player.Source = null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 换源失败不影响下一段：下一次会重新赋值。
            }

            stream.Dispose();
        }
    }

    /// <summary>暂停期间挂起，恢复或取消时返回。</summary>
    private async Task WaitWhilePausedAsync(CancellationToken token)
    {
        while (_paused)
        {
            TaskCompletionSource<bool>? resume = _resume;
            if (resume is null)
            {
                return;
            }

            await resume.Task.WaitAsync(token).ConfigureAwait(true);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "系统语音不可用，请检查“设置 > 时间和语言 > 语音”里是否安装了语音包。",
        _ => ex.Message,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        // _gate 不释放：合成可能还在后台线程上收尾（finally 里 Release），
        // 释放后调用会抛 ObjectDisposedException，而线程池上的未处理异常会直接终止进程。
        try
        {
            _synth?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 引擎已随进程一起回收，忽略。
        }

        _synth = null;

        try
        {
            _player.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 同上：释放是尽力而为的清理动作。
        }
    }
}
