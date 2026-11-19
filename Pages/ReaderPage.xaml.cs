using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using xinread.Rendering;
using xinread.Services;
using xinread.WeRead;

namespace xinread.Pages;

/// <summary>大声朗读在页面里的三种状态，用于切换工具栏按钮图标。</summary>
internal enum SpeechPhase
{
    Idle,
    Speaking,
    Paused,
}

/// <summary>
/// 阅读页：章节列表 + 正文。正文由 <see cref="WebReaderClient"/> 取到后转成 Markdown
/// （<see cref="ChapterMarkdown"/>），再解析成原生控件树（<see cref="MarkdownRenderer"/>）渲染，
/// 朗读交给 <see cref="SpeechReader"/>，全程不依赖 WebView2。
/// </summary>
public sealed partial class ReaderPage : Page
{
    private const int DefaultFontSize = 18;
    private const int MinFontSize = 12;
    private const int MaxFontSize = 32;
    private const int FontSizeStep = 2;

    private const double DefaultSpeechRate = 1.0;
    private const double DefaultSpeechVolume = 0.7;

    /// <summary>正文左右留白按窗口宽度取百分比，再夹在一个既不贴边也不太宽的范围内。</summary>
    private const double ContentSideMarginRatio = 0.022;
    private const double MinContentSideMargin = 16;
    private const double MaxContentSideMargin = 48;

    /// <summary>折叠阈值的兜底值；首次测量到工具栏自然宽度后会用它替换。</summary>
    private const double CompactToolbarWidth = 1080;

    /// <summary>折叠态下标题至少还留出的空间。</summary>
    private const double CompactHeaderMinWidth = 120;

    /// <summary>窄屏时标题最多显示的字数。</summary>
    private const int CompactHeaderChars = 5;

    private CancellationTokenSource _lifetime = new();
    private SpeechReader _speech = new();
    private bool _speechReleased;

    private IReadOnlyList<ChapterInfo> _chapters = [];
    private IReadOnlyList<RenderedBlock> _rendered = [];

    /// <summary>可朗读块的原始下标。朗读接口用“第几段”，渲染与高亮用“第几块”，靠它换算。</summary>
    private List<int> _speechBlocks = [];

    private IReadOnlyList<SpeechVoice> _speechVoices = [];
    private SpeechOptions _speechOptions = LoadSpeechOptions();
    private bool _voicesLoaded;

    private string _bookId = string.Empty;
    private string _format = string.Empty;

    /// <summary>访客模式下官网给的 readerId；为空表示登录态阅读。</summary>
    private string _readerId = string.Empty;

    /// <summary>是否游客试看：只放开前三章，并在超出时提示登录。</summary>
    private bool _guestMode;

    private int _currentIndex = -1;

    /// <summary>最近一次发起加载的章节下标，用于判断哪个请求才是最新的。</summary>
    private int _requestedIndex = -1;
    private int _fontSize = LoadFontSize();
    private SpeechPhase _speechPhase;
    private int _speechPosition;
    private int _highlightedBlock = -1;

    /// <summary>窗口宽时标题的完整文案；窄屏时截断到前几个字。</summary>
    private string _headerFull = string.Empty;

    /// <summary>当前工具栏是否处于折叠态，避免每次 SizeChanged 都重建菜单。</summary>
    private bool _toolbarCompact;

    /// <summary>宽屏工具栏排满一行所需的宽度，实测得到，用于决定何时折叠。</summary>
    private double _toolbarNaturalWidth;

    /// <summary>下一章预取任务；<see cref="_prefetchUid"/> 表示它对应哪一章。</summary>
    private Task<ChapterContent?>? _prefetchTask;
    private long _prefetchUid;
    private CancellationTokenSource? _prefetchCts;

    /// <summary>取上次调好的字号，没调过或存档越界时用默认值。</summary>
    private static int LoadFontSize()
    {
        int saved = App.Settings.FontSize;
        return saved is >= MinFontSize and <= MaxFontSize ? saved : DefaultFontSize;
    }

    private static SpeechOptions LoadSpeechOptions() => SanitizeSpeechOptions(App.Settings.Speech);

    /// <summary>存档缺字段或手改越界时逐项退回默认值；<paramref name="options"/> 为 null 也兜住。</summary>
    private static SpeechOptions SanitizeSpeechOptions(SpeechOptions? options)
    {
        options ??= new SpeechOptions();
        return new SpeechOptions
        {
            Rate = InRange(options.Rate, 0.5, 3.0) ? options.Rate : DefaultSpeechRate,
            Volume = InRange(options.Volume, 0.1, 1.0) ? options.Volume : DefaultSpeechVolume,
            Voice = string.IsNullOrWhiteSpace(options.Voice) ? null : options.Voice,
            AutoNextChapter = options.AutoNextChapter,
        };
    }

    /// <summary>存档里缺字段会反序列化成 0，用取值区间挡掉。</summary>
    private static bool InRange(double value, double min, double max) => value >= min && value <= max;

    public ReaderPage()
    {
        InitializeComponent();
        UpdateFontButtons();
        SetSpeechPhase(SpeechPhase.Idle);

        // 预览阶段拦截，避免 ScrollViewer 先按自己的步长滚一次再被我们覆盖。
        ReaderRoot.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnReaderPreviewKeyDown), true);

        // 系统切换深色 / 浅色模式时，正文里动态创建的数以千计的控件不会自动更新配色。
        ActualThemeChanged += OnActualThemeChanged;

        AttachSpeech();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 主题切换后重新给正文着色。控件树不重建，滚动位置、选中内容和朗读进度都保留。
    /// </summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ElementTheme theme = ActualTheme;

        // 先按新主题重算所有块的原色，再把朗读高亮重新盖上去。
        // 顺序不能反：ApplyTheme 会把引用 / 代码块刷回它们自己的底色。
        MarkdownRenderer.ApplyTheme(_rendered, theme);

        if (_highlightedBlock >= 0 && _highlightedBlock < _rendered.Count)
        {
            MarkdownRenderer.SetHighlight(_rendered[_highlightedBlock], highlighted: true, theme);
        }
    }

    private void AttachSpeech()
    {
        _speech.Configure(_speechOptions);
        _speech.ParagraphStarted += OnSpeechParagraphStarted;
        _speech.Finished += OnSpeechFinished;
        _speech.Failed += OnSpeechFailed;
    }

    /// <summary>
    /// 页面离开时令牌被取消、朗读器被释放，但 Frame 的返回栈会复用同一个页面实例
    /// （例如从阅读页去登录页再按返回）。重新进入时必须换一套干净的状态，
    /// 否则所有 await 都会立刻因令牌已取消而中断，页面停在“正在载入正文…”。
    /// </summary>
    private void ResetLifetime()
    {
        if (_lifetime.IsCancellationRequested)
        {
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
        }

        // 页面上次卸载时解除了主题订阅，复用同一实例前要重新挂上。
        ActualThemeChanged -= OnActualThemeChanged;
        ActualThemeChanged += OnActualThemeChanged;

        if (_speechReleased)
        {
            _speech = new SpeechReader();
            _speechReleased = false;
            AttachSpeech();
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ResetLifetime();

        if (e.Parameter is not string id || string.IsNullOrEmpty(id))
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }

            return;
        }

        // 已登录时传的是 bookId，未登录时传的是官网 readerId：两者形状不同，靠登录状态区分。
        if (App.Session.IsSignedIn)
        {
            _bookId = id;
        }
        else
        {
            _readerId = id;
            _guestMode = true;
        }

        await LoadBookAsync();
    }

    private async Task LoadBookAsync()
    {
        LoadingRing.IsActive = true;
        SetChapterNavigationEnabled(false);

        try
        {
            if (_guestMode)
            {
                GuestBookDetail detail = await App.Guest.GetBookAsync(_readerId, _lifetime.Token);
                _bookId = detail.BookId;
                _format = detail.Format;
                SetHeader(string.IsNullOrEmpty(detail.Author)
                    ? detail.Title
                    : $"{detail.Title} · {detail.Author}");

                _chapters = detail.Chapters;
                if (_chapters.Count == 0)
                {
                    ShowEmptyBook();
                    return;
                }

                ChapterBox.ItemsSource = BuildChapterTitles();
                SignInBar.IsOpen = true;
                await OpenChapterAsync(0);
                return;
            }

            BookInfo info = await App.Session.Mobile.GetBookInfoAsync(_bookId, _lifetime.Token);
            _format = info.Format;
            SetHeader(string.IsNullOrEmpty(info.Author) ? info.Title : $"{info.Title} · {info.Author}");

            _chapters = await App.Session.Mobile.GetChaptersAsync(_bookId, _lifetime.Token);
            if (_chapters.Count == 0)
            {
                ShowEmptyBook();
                return;
            }

            ChapterBox.ItemsSource = BuildChapterTitles();

            long? saved = App.Positions.GetChapter(_bookId);
            if (saved is null)
            {
                (long? serverChapter, _) = await App.Session.Mobile.GetProgressAsync(_bookId, _lifetime.Token);
                saved = serverChapter;
            }

            int index = saved is null
                ? -1
                : _chapters.ToList().FindIndex(c => c.ChapterUid == saved);

            await OpenChapterAsync(index < 0 ? 0 : index);
        }
        catch (WeReadAuthException)
        {
            await FallBackToGuestAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, $"打开失败：{ex.Message}");
            Placeholder.Text = "无法打开这本书。";
            Placeholder.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    /// <summary>没有可读章节时统一收尾：清空正文与朗读入口，只留一条提示。</summary>
    private void ShowEmptyBook()
    {
        _chapters = [];
        _currentIndex = -1;
        ChapterBox.ItemsSource = null;
        ShowContent(string.Empty);
        Placeholder.Text = "这本书没有可读取的章节。";
        Placeholder.Visibility = Visibility.Visible;
        UpdateChapterButtons();
    }

    /// <summary>会话失效时不再强制跳登录页，改成游客视角重新打开这本书。</summary>
    private async Task FallBackToGuestAsync()
    {
        if (_guestMode)
        {
            ShowNotice(InfoBarSeverity.Error, "试看失败，请稍后重试。");
            return;
        }

        // 离开登录态之前先记下 bookId：Encode 需要的是书籍 id，不是 readerId。
        string bookId = _bookId;
        await App.Session.SignOutAsync();

        if (string.IsNullOrEmpty(bookId))
        {
            ShowNotice(InfoBarSeverity.Error, "登录已失效，请重新登录。");
            return;
        }

        _guestMode = true;
        _readerId = ReaderIdEncoder.Encode(bookId);
        await LoadBookAsync();
    }

    /// <summary>txt 书的多数章节没有标题，用章序号兜底，避免目录出现空白项。</summary>
    private static string DisplayTitle(ChapterInfo chapter, int index)
        => string.IsNullOrWhiteSpace(chapter.Title) ? $"第 {index + 1} 节" : chapter.Title;

    /// <summary>章节下拉的候选项：访客只显示可试读的章节，第 4 章起直接隐藏。</summary>
    private List<string> BuildChapterTitles()
    {
        int limit = _guestMode ? GuestLimit : _chapters.Count;

        var titles = new List<string>(limit);
        for (int i = 0; i < limit; i++)
        {
            titles.Add(DisplayTitle(_chapters[i], i));
        }

        return titles;
    }

    /// <summary>
    /// 未登录时能读到的章节数（含版权页等前置页）。章节列表为空时按 0 处理，
    /// 避免各处重复写 <see cref="Math.Min"/> 导致判定不一致。
    /// </summary>
    private int GuestLimit => Math.Min(ShelfPage.GuestChapterLimit, _chapters.Count);

    /// <summary>访客读到试读末章：第 4 章起的任何翻章请求都要拦下来。</summary>
    private bool IsBeyondGuestLimit(int index) => _guestMode && index >= GuestLimit;

    /// <summary>打开指定章节，返回是否真的打开成功。</summary>
    private async Task<bool> OpenChapterAsync(int index)
    {
        if (index < 0 || index >= _chapters.Count)
        {
            return false;
        }

        // 访客只能试读前三章，再往后就提示登录。
        if (IsBeyondGuestLimit(index))
        {
            // 下拉框可能已经被用户改到越界项，退回当前实际章节，避免显示与实际不符。
            RestoreChapterSelection();
            ShowSignInPrompt();
            return false;
        }

        ChapterInfo chapter = _chapters[index];
        int previousIndex = _currentIndex;
        _currentIndex = index;
        _requestedIndex = index;
        ChapterBox.SelectedIndex = index;

        // 跳到的不是预取中的那一章，就把旧预取停掉，省下带宽。
        if (_prefetchUid != 0 && _prefetchUid != chapter.ChapterUid)
        {
            CancelPrefetch();
        }

        LoadingRing.IsActive = true;
        Placeholder.Text = "正在载入正文…";
        Placeholder.Visibility = Visibility.Visible;
        SetChapterNavigationEnabled(false);

        try
        {
            ChapterContent content = await GetChapterContentAsync(chapter).ConfigureAwait(true);

            // 预取让翻章变得很快，连点时可能已有更新的目标章节，别再拿旧结果覆盖。
            if (index != _currentIndex)
            {
                return false;
            }

            if (content.IsFullText)
            {
                NoticeBar.IsOpen = false;
            }
            else
            {
                ShowNotice(InfoBarSeverity.Warning, "该章节为试读节选，完整内容需要在微信读书中购买。");
            }

            ShowContent(content.Markdown);
            Placeholder.Visibility = Visibility.Collapsed;

            // 访客不记录进度，避免污染登录后的书架进度。
            if (!_guestMode)
            {
                await App.Positions.SetChapterAsync(_bookId, chapter.ChapterUid);
            }

            StartPrefetchNextChapter(index);
            return true;
        }
        catch (ChapterFormatException ex)
        {
            ShowNotice(InfoBarSeverity.Error, $"正文解析失败：{ex.Message}");
            Placeholder.Text = "无法解析这一章。";
            Placeholder.Visibility = Visibility.Visible;
            RevertToPreviousChapter(index, previousIndex);
            return false;
        }
        catch (WeReadAuthException)
        {
            await FallBackToGuestAsync();
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, $"读取正文失败：{ex.Message}");
            Placeholder.Text = "无法读取这一章。";
            Placeholder.Visibility = Visibility.Visible;
            RevertToPreviousChapter(index, previousIndex);
            return false;
        }
        finally
        {
            // 连点翻章时旧请求可能晚于新请求收尾，只让最新的那次请求关掉加载状态。
            if (_requestedIndex == index)
            {
                LoadingRing.IsActive = false;
                UpdateChapterButtons();
            }
        }
    }

    /// <summary>
    /// 章节打开失败时退回上一章，避免翻章按钮按已经失败的章节继续计算。
    /// 期间用户已经切到别的章节就不再回退，否则会把新章节的选择覆盖掉。
    /// </summary>
    private void RevertToPreviousChapter(int failedIndex, int previousIndex)
    {
        if (_currentIndex != failedIndex)
        {
            return;
        }

        _currentIndex = previousIndex;
        RestoreChapterSelection();
    }

    /// <summary>
    /// 把章节下拉框拨回当前实际章节。一次都没打开成功时清空选择，
    /// 这样下拉框显示占位文案，而不是停在打开的失败项上。
    /// </summary>
    private void RestoreChapterSelection()
        => ChapterBox.SelectedIndex = _currentIndex >= 0 && _currentIndex < ChapterBox.Items.Count
            ? _currentIndex
            : -1;

    /// <summary>更新翻章按钮的可用状态：访客读到试读末章时「下一章」置灰。</summary>
    private void UpdateChapterButtons()
    {
        bool guestLimitReached = IsBeyondGuestLimit(_currentIndex + 1);
        bool canPrev = _currentIndex > 0;
        bool canNext = _currentIndex < _chapters.Count - 1 && !guestLimitReached;

        SetChapterNavigationEnabled(canPrev, canNext);

        // 章节列表可能刚从空变成有内容，章末翻章入口的显隐要跟着重算。
        UpdateChapterNavVisibility();

        string? nextTip = guestLimitReached ? $"未登录只能试看前 {ShelfPage.GuestChapterLimit} 章。" : null;
        ToolTipService.SetToolTip(NextButton, nextTip);
        ToolTipService.SetToolTip(BottomNextButton, nextTip);
    }

    /// <summary>加载期间把上下两处翻章按钮一起置灰，避免连点产生并发请求。</summary>
    private void SetChapterNavigationEnabled(bool enabled) => SetChapterNavigationEnabled(enabled, enabled);

    /// <summary>章末按钮与工具栏按钮必须同步，否则两处行为会不一致。</summary>
    private void SetChapterNavigationEnabled(bool canPrev, bool canNext)
    {
        PrevButton.IsEnabled = canPrev;
        NextButton.IsEnabled = canNext;
        BottomPrevButton.IsEnabled = canPrev;
        BottomNextButton.IsEnabled = canNext;
    }

    /// <summary>
    /// 章末翻章按钮只在工具栏折叠成 ☰、翻章入口不在屏幕上时才出现；
    /// 工具栏展开时它和底部按钮重复，藏起来少占正文空间。
    /// </summary>
    private void UpdateChapterNavVisibility()
        => ChapterNav.Visibility = _toolbarCompact && _chapters.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void ShowContent(string markdown)
    {
        // 先按旧控件树还原高亮，再换 _rendered：否则会拿旧下标去索引新块。
        ClearHighlight();
        _speechPosition = 0;

        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(markdown);
        _rendered = MarkdownRenderer.Render(ContentPanel, blocks, _fontSize);

        // 只有有文字的块才朗读，但要记住块下标，朗读时才能定位高亮。
        _speechBlocks = [];
        for (int i = 0; i < _rendered.Count; i++)
        {
            if (_rendered[i].PlainText.Length > 0)
            {
                _speechBlocks.Add(i);
            }
        }

        UpdateSpeakButtonState();

        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);

        _ = LoadImagesAsync(_rendered);
    }

    /// <summary>逐张补齐本页插图；换章后旧图不再下载，避免翻得快时堆积无用请求。</summary>
    private async Task LoadImagesAsync(IReadOnlyList<RenderedBlock> blocks)
    {
        foreach (RenderedBlock block in blocks)
        {
            if (block.Picture is null || string.IsNullOrEmpty(block.ImageUrl))
            {
                continue;
            }

            bool loaded = await RemoteImageLoader.ApplyAsync(block.Picture, block.ImageUrl, _lifetime.Token);

            // 换章后旧图不再影响新页面（控件树已被替换），但也不要更新已失效的块。
            if (!ReferenceEquals(blocks, _rendered))
            {
                return;
            }

            MarkdownRenderer.ApplyImageState(block, loaded);
        }
    }

    /// <summary>取正文：优先用预热好的下一章结果，避免重复请求。</summary>
    private async Task<ChapterContent> GetChapterContentAsync(ChapterInfo chapter)
    {
        if (_prefetchUid == chapter.ChapterUid && _prefetchTask is { } pending)
        {
            // 接过预取任务后立即摘掉引用，避免取消逻辑与这里重复释放同一个 CTS。
            CancellationTokenSource? cts = _prefetchCts;
            _prefetchTask = null;
            _prefetchCts = null;
            _prefetchUid = 0;

            try
            {
                ChapterContent? prefetched = await pending;
                if (prefetched is not null)
                {
                    return prefetched;
                }
            }
            finally
            {
                cts?.Dispose();
            }

            // 预取失败（或已取消）时静默重试一次，错误交给正式请求去提示。
        }

        if (_guestMode)
        {
            return await App.Guest.GetChapterAsync(_bookId, chapter.ChapterUid, _format, _lifetime.Token);
        }

        return await App.Session.Reader.GetChapterAsync(
            _bookId,
            chapter.ChapterUid,
            _format,
            _lifetime.Token);
    }

    /// <summary>
    /// 后台预热下一章：正文与插图都在读本章时就下好，点「下一章」基本只剩渲染。
    /// 取消、失败一律返回 null，不向 UI 抛异常；正式打开时会重新拉取。
    /// </summary>
    private void StartPrefetchNextChapter(int currentIndex)
    {
        int nextIndex = currentIndex + 1;
        if (nextIndex < 0 || nextIndex >= _chapters.Count)
        {
            return;
        }

            // 访客能看的最后一章之后不再预取，省流量也避免必然失败的请求。
            if (IsBeyondGuestLimit(nextIndex))
            {
                return;
            }
        ChapterInfo next = _chapters[nextIndex];
        if (_prefetchTask is not null && _prefetchUid == next.ChapterUid)
        {
            return;
        }

        CancelPrefetch();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _prefetchCts = cts;
        _prefetchUid = next.ChapterUid;
        _prefetchTask = PrefetchChapterAsync(next, cts.Token);
    }

    private async Task<ChapterContent?> PrefetchChapterAsync(ChapterInfo chapter, CancellationToken cancellationToken)
    {
        try
        {
                ChapterContent content = _guestMode
                    ? await App.Guest.GetChapterAsync(_bookId, chapter.ChapterUid, _format, cancellationToken)
                    : await App.Session.Reader.GetChapterAsync(_bookId, chapter.ChapterUid, _format, cancellationToken);

            // 正文一拿到就先交出去，插图在后台继续下；正式翻到该章时大多已经命中缓存。
            // 图片预热用页面令牌：即使这个预取任务随后被取消，已开始的下载也能完成入缓存。
            _ = PrefetchImagesAsync(content.Markdown, _lifetime.Token);
            return content;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // 预取只是提速手段，失败不该影响阅读体验；正式打开时会重新拉取并提示错误。
            return null;
        }
    }

    /// <summary>预取正文里的插图，让翻到下一章时图片已经在缓存里。</summary>
    private static async Task PrefetchImagesAsync(string markdown, CancellationToken cancellationToken)
    {
        try
        {
            foreach (MarkdownBlock block in MarkdownParser.Parse(markdown))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (block.Kind != MarkdownBlockKind.Image || string.IsNullOrEmpty(block.Text))
                {
                    continue;
                }

                await RemoteImageLoader.PrefetchAsync(block.Text, cancellationToken);
            }
        }
        catch (Exception)
        {
            // 后台预热失败无需上报，翻到该章时正式加载会各自重试并折叠失败的图。
        }
    }

    /// <summary>取消未完成的预取；已发起的图片下载会继续并进缓存，不影响下次打开。</summary>
    private void CancelPrefetch()
    {
        CancellationTokenSource? cts = _prefetchCts;
        Task<ChapterContent?>? task = _prefetchTask;
        _prefetchCts = null;
        _prefetchTask = null;
        _prefetchUid = 0;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();

        // 任务可能还在用这个令牌，等它收尾再释放，避免 ObjectDisposedException。
        if (task is null)
        {
            cts.Dispose();
            return;
        }

        _ = task.ContinueWith(
            _ => cts.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>正文跟着窗口宽度走，窗口越宽左右留白按比例收缩，避免两栏大空白。</summary>
    private void OnContentScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateContentPadding(e.NewSize.Width);

    private void UpdateContentPadding(double viewportWidth)
    {
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

        double side = Math.Clamp(
            viewportWidth * ContentSideMarginRatio,
            MinContentSideMargin,
            MaxContentSideMargin);

        Thickness padding = ContentPanel.Padding;
        if (Math.Abs(padding.Left - side) < 0.5 && Math.Abs(padding.Right - side) < 0.5)
        {
            return;
        }

        ContentPanel.Padding = new Thickness(side, padding.Top, side, padding.Bottom);
    }

    private void UpdateFontButtons()
    {
        FontDecreaseButton.IsEnabled = _fontSize > MinFontSize;
        FontIncreaseButton.IsEnabled = _fontSize < MaxFontSize;
    }

    /// <summary>设置标题文案。窄屏时只保留前几个字，宽屏时用完整文案。</summary>
    private void SetHeader(string text)
    {
        _headerFull = text ?? string.Empty;
        ApplyHeaderText();
    }

    private void ApplyHeaderText()
    {
        if (_toolbarCompact)
        {
            // 书名通常在最前面，截到前 5 个字足以辨认，也不至于把工具栏挤走。
            string head = _headerFull.Length > CompactHeaderChars
                ? _headerFull[..CompactHeaderChars] + "…"
                : _headerFull;
            HeaderText.Text = head;
        }
        else
        {
            HeaderText.Text = _headerFull;
        }
    }

    /// <summary>
    /// 窗口变窄时把工具按钮折进 ☰：横向工具栏隐藏，菜单里改为竖向排列同样的操作。
    /// 手动构造菜单项而不复用原控件，避免一个控件同时挂在两处。
    /// </summary>
    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 先量一次横向工具栏的自然宽度，之后按「放得下工具栏 + 还留得住标题」判断。
        if (!_toolbarCompact && ToolbarActions.ActualWidth > 0)
        {
            double natural = ToolbarActions.ActualWidth + MenuButton.ActualWidth + CompactHeaderMinWidth;
            if (natural > _toolbarNaturalWidth)
            {
                _toolbarNaturalWidth = natural;
            }
        }

        double threshold = _toolbarNaturalWidth > 0 ? _toolbarNaturalWidth : CompactToolbarWidth;
        bool compact = e.NewSize.Width < threshold;
        if (compact == _toolbarCompact)
        {
            return;
        }

        _toolbarCompact = compact;
        ToolbarActions.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        if (!compact && ActionsFlyout.IsOpen)
        {
            ActionsFlyout.Hide();
        }

        // 折叠状态变了，章末翻章按钮该跟着出现/隐藏。
        UpdateChapterNavVisibility();
        ApplyHeaderText();
    }

    /// <summary>按下 ☰ 时按当前状态重建菜单，保证字号按钮的可用状态与朗读图标是实时的。</summary>
    private void OnActionsFlyoutOpening(object sender, object e)
    {
        // 宽度随窗口收敛：窄屏时菜单不该把右侧挤到屏幕外。
        double width = Math.Clamp(ToolbarHost.ActualWidth - 48, 200, 260);
        var panel = new StackPanel { Spacing = 8, Width = width };

        panel.Children.Add(BuildActionRow("上一章", CloseAnd(OnPrevClick), PrevButton.IsEnabled, "向左翻一章"));
        panel.Children.Add(BuildActionRow("下一章", CloseAnd(OnNextClick), NextButton.IsEnabled, "向右翻一章"));

        if (ChapterBox.Items.Count > 0)
        {
            var chapterPicker = new ComboBox
            {
                ItemsSource = ChapterBox.ItemsSource,
                SelectedIndex = ChapterBox.SelectedIndex,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Header = "章节",
            };
            chapterPicker.SelectionChanged += (_, _) =>
            {
                if (chapterPicker.SelectedIndex >= 0 && chapterPicker.SelectedIndex != _currentIndex)
                {
                    ChapterBox.SelectedIndex = chapterPicker.SelectedIndex;
                    ActionsFlyout.Hide();
                }
            };

            panel.Children.Add(chapterPicker);
        }

        var fontRow = new Grid { ColumnSpacing = 8 };
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Button decrease = BuildActionRow("A−", CloseAnd(OnDecreaseFontClick), FontDecreaseButton.IsEnabled, "减小字号");
        Button increase = BuildActionRow("A+", CloseAnd(OnIncreaseFontClick), FontIncreaseButton.IsEnabled, "增大字号");
        Grid.SetColumn(increase, 1);
        fontRow.Children.Add(decrease);
        fontRow.Children.Add(increase);
        panel.Children.Add(fontRow);

        string glyph = _speechPhase switch
        {
            SpeechPhase.Speaking => "\u23F8",
            SpeechPhase.Paused => "\u25B6",
            _ => "\U0001F50A",
        };

        string speakLabel = _speechPhase switch
        {
            SpeechPhase.Speaking => "暂停",
            SpeechPhase.Paused => "继续",
            _ => "朗读",
        };

        // 与上一行的字号并排：朗读与设置各占一半，兼顾触控与就近操作。
        var speakRow = new Grid { ColumnSpacing = 8 };
        speakRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        speakRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 与工具栏按钮保持一致：没有可朗读正文且不在朗读时不可点。
        bool speakEnabled = SpeakButton.IsEnabled;
        Button speak = BuildActionRow(
            $"{glyph} {speakLabel}",
            CloseAnd(OnSpeakClick),
            speakEnabled,
            speakEnabled ? "朗读当前章节" : "这一章没有可朗读的正文");

        Button options = BuildActionRow("\u2699 设置", null, isEnabled: true, "朗读设置：语音、语速、音量");
        options.Click += (_, _) =>
        {
            ActionsFlyout.Hide();
            // ☰ 是此刻唯一可见的锚点，折叠态的朗读按钮本身不可见。
            SpeakSettingsButton.Flyout?.ShowAt(MenuButton);
        };

        Grid.SetColumn(options, 1);
        speakRow.Children.Add(speak);
        speakRow.Children.Add(options);
        panel.Children.Add(speakRow);

        ActionsFlyout.Content = panel;
    }

    /// <summary>包一层：先收起菜单再执行原操作，避免菜单浮在正文上挡住翻章结果。</summary>
    private RoutedEventHandler CloseAnd(RoutedEventHandler handler) => (sender, e) =>
    {
        ActionsFlyout.Hide();
        handler(sender, e);
    };

    private Button BuildActionRow(string text, RoutedEventHandler? click, bool isEnabled, string description)
    {
        var button = new Button
        {
            Content = text,
            IsEnabled = isEnabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 0,
        };

        // 禁用态的 Flyout 按钮文字会淡到几乎看不见，这里保持可读的灰色并说明原因。
        // 用 TryGetValue 而不是索引器：主题资源在某些环境下可能取不到，索引器会直接抛异常。
        if (!isEnabled)
        {
            button.Foreground = DisabledTextBrush();
            AutomationProperties.SetName(button, $"{description}（当前不可用）");
        }
        else
        {
            AutomationProperties.SetName(button, description);
        }

        if (click is not null)
        {
            button.Click += click;
        }

        return button;
    }

    /// <summary>禁用态文字画刷；资源缺失时退回半透明灰，避免取不到就抛异常。</summary>
    private static Brush DisabledTextBrush()
    {
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue("TextFillColorDisabledBrush", out object? value)
            && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(0x5C, 0x80, 0x80, 0x80));
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(ShelfPage));
        }
    }

    /// <summary>把初始焦点放到正文上，进页面就能直接用键盘翻章翻页。</summary>
    private void OnReaderRootLoaded(object sender, RoutedEventArgs e) => ContentScroller.Focus(FocusState.Programmatic);

    /// <summary>游客读到试读上限时的提示：把「试看模式」提示条打开，不额外再弹一条重复文案。</summary>
    private void ShowSignInPrompt()
    {
        SignInBar.IsOpen = true;
    }

    private void OnSignInClick(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(LoginPage));

    /// <summary>
    /// 键盘阅读：←/→ 翻章，↑/↓、PgUp/PgDn 翻页。焦点在输入类控件上时不抢键，
    /// 否则章节下拉框和搜索框就没法用方向键了。
    /// </summary>
    private void OnReaderPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ShouldLetFocusedControlHandle(e.OriginalSource as DependencyObject))
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left:
                e.Handled = true;
                if (PrevButton.IsEnabled)
                {
                    _ = GoToChapterAsync(_currentIndex - 1);
                }

                break;

            case VirtualKey.Right:
                e.Handled = true;
                if (NextButton.IsEnabled)
                {
                    _ = GoToChapterAsync(_currentIndex + 1);
                }
                else if (IsBeyondGuestLimit(_currentIndex + 1))
                {
                    ShowSignInPrompt();
                }

                break;

            case VirtualKey.Up:
            case VirtualKey.PageUp:
                e.Handled = true;
                PageBy(ContentScroller, -1);
                break;

            case VirtualKey.Down:
            case VirtualKey.PageDown:
                e.Handled = true;
                PageBy(ContentScroller, 1);
                break;
        }
    }

    /// <summary>按视口高度的九成翻页，留一点重叠，读者不会丢掉上下文的接缝。</summary>
    private static void PageBy(ScrollViewer scroller, int direction)
    {
        if (scroller.ScrollableHeight <= 0)
        {
            return;
        }

        double step = Math.Max(scroller.ViewportHeight * 0.9, 60);
        double target = Math.Clamp(
            scroller.VerticalOffset + (step * direction),
            0,
            scroller.ScrollableHeight);

        scroller.ChangeView(null, target, null, disableAnimation: false);
    }

    /// <summary>输入类控件（下拉框、搜索框）要自己用方向键，别在这里拦下来。</summary>
    private bool ShouldLetFocusedControlHandle(DependencyObject? source)
    {
        // 下拉框展开时焦点在 Popup 里的候选项上，祖先链里找不到 ComboBox，只能看开合状态。
        if (ChapterBox.IsDropDownOpen)
        {
            return true;
        }

        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject ?? source;

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or ComboBox or AutoSuggestBox or FlyoutPresenter)
            {
                return true;
            }
        }

        return false;
    }

    private async void OnPrevClick(object sender, RoutedEventArgs e) => await GoToChapterAsync(_currentIndex - 1);

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        // 试读上限时给出明确原因，而不是悄无声息地不响应。
        if (IsBeyondGuestLimit(_currentIndex + 1))
        {
            ShowSignInPrompt();
            return;
        }

        _ = GoToChapterAsync(_currentIndex + 1);
    }

    private async void OnChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ChapterBox.SelectedIndex >= 0 && ChapterBox.SelectedIndex != _currentIndex)
        {
            await GoToChapterAsync(ChapterBox.SelectedIndex);
        }
    }

    /// <summary>用户主动切章：先停掉朗读，否则旧章节的段落会继续从新页面里找位置高亮。</summary>
    private async Task GoToChapterAsync(int index)
    {
        // 越界（例如在第一章按了 ←）时不该停掉正在进行的朗读。
        if (index < 0 || index >= _chapters.Count)
        {
            return;
        }

        StopSpeech();
        await OpenChapterAsync(index);
    }

    private void OnIncreaseFontClick(object sender, RoutedEventArgs e) => SetFontSize(_fontSize + FontSizeStep);

    private void OnDecreaseFontClick(object sender, RoutedEventArgs e) => SetFontSize(_fontSize - FontSizeStep);

    /// <summary>字号夹到取值范围内并记住。只改已有控件的字号，不重建控件树、不丢滚动位置。</summary>
    private void SetFontSize(int size)
    {
        _fontSize = Math.Clamp(size, MinFontSize, MaxFontSize);
        UpdateFontButtons();
        MarkdownRenderer.ApplyFontSize(_rendered, _fontSize);
        App.Settings.FontSize = _fontSize;
    }

    private void OnSpeakClick(object sender, RoutedEventArgs e)
    {
        switch (_speechPhase)
        {
            case SpeechPhase.Idle:
                if (_speechBlocks.Count == 0)
                {
                    ShowNotice(InfoBarSeverity.Informational, "这一章没有可朗读的正文。");
                    return;
                }

                SetSpeechPhase(SpeechPhase.Speaking);
                StartSpeech(_speechPosition);
                break;

            case SpeechPhase.Speaking:
                // Pause 返回 false 只说明朗读已经结束或被取消（_run 已被清掉），
                // 段间合成空档仍会记住暂停标志，等合成完成停在该段开头。
                if (!_speech.Pause())
                {
                    StopSpeech();
                    return;
                }

                SetSpeechPhase(SpeechPhase.Paused);
                break;

            default:
                if (!_speech.Resume())
                {
                    // 暂停标记已经丢失（例如朗读已自行结束），从当前段重新开始。
                    SetSpeechPhase(SpeechPhase.Speaking);
                    StartSpeech(_speechPosition);
                    return;
                }

                SetSpeechPhase(SpeechPhase.Speaking);
                break;
        }
    }

    /// <summary>从指定段落开始朗读；改参数导致的重新朗读也走这里，保持当前进度。</summary>
    private void StartSpeech(int startIndex)
    {
        if (_speechBlocks.Count == 0)
        {
            StopSpeech();
            return;
        }

        var paragraphs = new List<string>(_speechBlocks.Count);
        foreach (int blockIndex in _speechBlocks)
        {
            paragraphs.Add(_rendered[blockIndex].PlainText);
        }

        _speech.Configure(_speechOptions);
        _speechPosition = Math.Clamp(startIndex, 0, paragraphs.Count - 1);
        _speech.Start(paragraphs, _speechPosition);
    }

    /// <summary>停掉朗读并清掉高亮，回到空闲状态。</summary>
    private void StopSpeech()
    {
        _speech.Stop();
        ClearHighlight();
        SetSpeechPhase(SpeechPhase.Idle);
    }

    /// <summary>朗读推进到某一段：高亮并滚到可见位置。</summary>
    private void OnSpeechParagraphStarted(int index)
    {
        _speechPosition = index;
        if (index < 0 || index >= _speechBlocks.Count)
        {
            return;
        }

        int blockIndex = _speechBlocks[index];
        HighlightBlock(blockIndex);
        _rendered[blockIndex].Container.StartBringIntoView(new BringIntoViewOptions
        {
            VerticalAlignmentRatio = 0.25,
            AnimationDesired = true,
        });
    }

    private void HighlightBlock(int index)
    {
        ClearHighlight();
        if (index < 0 || index >= _rendered.Count)
        {
            return;
        }

        _highlightedBlock = index;
        MarkdownRenderer.SetHighlight(_rendered[index], highlighted: true);
    }

    private void ClearHighlight()
    {
        if (_highlightedBlock >= 0 && _highlightedBlock < _rendered.Count)
        {
            MarkdownRenderer.SetHighlight(_rendered[_highlightedBlock], highlighted: false);
        }

        _highlightedBlock = -1;
    }

    private void SetSpeechPhase(SpeechPhase phase)
    {
        _speechPhase = phase;

        (string glyph, string label, string tip) = phase switch
        {
            SpeechPhase.Speaking => ("\u23F8", "暂停朗读", "暂停朗读"),
            SpeechPhase.Paused => ("\u25B6", "继续朗读", "继续朗读"),
            _ => ("\U0001F50A", "大声朗读", "大声朗读"),
        };

        SpeakGlyph.Text = glyph;
        SpeakLabel.Text = label;
        ToolTipService.SetToolTip(SpeakButton, tip);
        AutomationProperties.SetName(SpeakButton, tip);
        UpdateSpeakButtonState();
    }

    /// <summary>
    /// 没有可朗读正文时把朗读按钮置灰，避免点了才提示。
    /// 朗读进行中即使块列表为空（例如刚切章）也不要改动状态，交给朗读流程自己收尾。
    /// </summary>
    private void UpdateSpeakButtonState()
    {
        bool hasContent = _speechBlocks.Count > 0;
        bool enabled = hasContent || _speechPhase != SpeechPhase.Idle;
        SpeakButton.IsEnabled = enabled;

        if (!hasContent)
        {
            ToolTipService.SetToolTip(SpeakButton, "这一章没有可朗读的正文。");
        }
    }

    private void OnSpeakOptionsOpening(object sender, object e)
    {
        AutoNextMenuItem.IsChecked = _speechOptions.AutoNextChapter;
        StopSpeakMenuItem.IsEnabled = _speechPhase != SpeechPhase.Idle;
        LoadVoices();
        UpdateSpeechMenuChecks();
    }

    private void UpdateSpeechMenuChecks()
    {
        SpeechOptions options = _speechOptions;
        RateSlow.IsChecked = Near(options.Rate, 0.7);
        RateNormal.IsChecked = Near(options.Rate, 1.0);
        RateFast.IsChecked = Near(options.Rate, 1.3);
        RateFaster.IsChecked = Near(options.Rate, 1.6);
        VolumeQuiet.IsChecked = Near(options.Volume, 0.3);
        VolumeNormal.IsChecked = Near(options.Volume, 0.7);
        VolumeLoud.IsChecked = Near(options.Volume, 1.0);

        foreach (object item in VoiceMenu.Items)
        {
            if (item is RadioMenuFlyoutItem { Tag: string id } radio)
            {
                // Tag 为空表示“系统默认语音”。
                radio.IsChecked = id.Length == 0
                    ? options.Voice is null
                    : string.Equals(id, options.Voice, StringComparison.Ordinal);
            }
        }
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) < 0.01;

    /// <summary>系统语音列表在一次运行内不会变，只在首次打开菜单时取一次。</summary>
    private void LoadVoices()
    {
        if (_voicesLoaded)
        {
            return;
        }

        _voicesLoaded = true;
        _speechVoices = SpeechReader.GetVoices();
        RebuildVoiceMenu();
    }

    private void RebuildVoiceMenu()
    {
        VoiceMenu.Items.Clear();

        var auto = new RadioMenuFlyoutItem
        {
            Text = DescribeDefaultVoice(),
            GroupName = "SpeechVoice",
            IsChecked = _speechOptions.Voice is null,
            Tag = string.Empty,
        };
        auto.Click += OnSpeechVoiceClick;
        VoiceMenu.Items.Add(auto);

        if (_speechVoices.Count == 0)
        {
            VoiceMenu.Items.Add(new MenuFlyoutItem { Text = "（未安装系统语音）", IsEnabled = false });
            return;
        }

        foreach (SpeechVoice voice in _speechVoices)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{voice.Name}（{voice.Lang}）",
                GroupName = "SpeechVoice",
                IsChecked = string.Equals(voice.Id, _speechOptions.Voice, StringComparison.Ordinal),
                Tag = voice.Id,
            };
            item.Click += OnSpeechVoiceClick;
            VoiceMenu.Items.Add(item);
        }
    }

    /// <summary>“系统默认”这一项直接显示实际会用的语音名，省得用户猜。</summary>
    private static string DescribeDefaultVoice()
    {
        string name = SpeechReader.DefaultVoiceName();
        return name.Length == 0 ? "系统默认语音" : $"系统默认语音（{name}）";
    }

    private void OnSpeechRateClick(object sender, RoutedEventArgs e)
    {
        if (TryGetTag(sender, out double rate))
        {
            ApplySpeechOptions(_speechOptions with { Rate = rate });
        }
    }

    private void OnSpeechVolumeClick(object sender, RoutedEventArgs e)
    {
        if (TryGetTag(sender, out double volume))
        {
            ApplySpeechOptions(_speechOptions with { Volume = volume });
        }
    }

    private void OnSpeechVoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string id })
        {
            ApplySpeechOptions(_speechOptions with { Voice = string.IsNullOrEmpty(id) ? null : id });
        }
    }

    private void OnAutoNextClick(object sender, RoutedEventArgs e)
        => ApplySpeechOptions(_speechOptions with { AutoNextChapter = AutoNextMenuItem.IsChecked });

    private void OnStopSpeakClick(object sender, RoutedEventArgs e) => StopSpeech();

    private static bool TryGetTag(object sender, out double value)
    {
        value = 0;
        return sender is FrameworkElement { Tag: string raw }
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>参数改动立刻生效并落盘；正在朗读时从当前段重开，让新参数马上能听到。</summary>
    private void ApplySpeechOptions(SpeechOptions options)
    {
        _speechOptions = SanitizeSpeechOptions(options);
        App.Settings.Speech = _speechOptions;
        _speech.Configure(_speechOptions);

        if (_speechPhase == SpeechPhase.Speaking)
        {
            StartSpeech(_speechPosition);
        }
    }

    private void OnSpeechFailed(string message)
    {
        ShowNotice(InfoBarSeverity.Error, $"朗读失败：{message}");
        StopSpeech();
    }

    /// <summary>读完当前章后按需翻到下一章接着读；取消自动翻章或已是最后一章时停下。</summary>
    private async void OnSpeechFinished()
    {
        if (_speechPhase != SpeechPhase.Speaking
            || !_speechOptions.AutoNextChapter
            || IsBeyondGuestLimit(_currentIndex + 1)
            || _currentIndex >= _chapters.Count - 1)
        {
            StopSpeech();
            return;
        }

        // 先归位再开下一章：翻章期间滚动与高亮都指向新内容，旧的朗读状态必须清掉。
        StopSpeech();
        bool opened = await OpenChapterAsync(_currentIndex + 1);
        if (!opened)
        {
            return;
        }

        SetSpeechPhase(SpeechPhase.Speaking);
        StartSpeech(0);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 离开阅读页不能留下还在说话的语音，也别让预取继续跑。
        CancelPrefetch();
        ActualThemeChanged -= OnActualThemeChanged;
        _speech.ParagraphStarted -= OnSpeechParagraphStarted;
        _speech.Finished -= OnSpeechFinished;
        _speech.Failed -= OnSpeechFailed;
        _speech.Dispose();
        _speechReleased = true;
        _lifetime.Cancel();
    }

    private void ShowNotice(InfoBarSeverity severity, string message)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}
