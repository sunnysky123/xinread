using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using xinread.WeRead;

namespace xinread.Pages;

/// <summary>书架 / 首页网格中的一本书。</summary>
public sealed class ShelfBookItem
{
    private ShelfBookItem(
        string bookId,
        string? readerId,
        string title,
        string author,
        string coverUrl,
        bool finished,
        DateTimeOffset? lastReadAt,
        string badge)
    {
        BookId = bookId;
        ReaderId = readerId;
        Title = title;
        Author = author;
        CoverUrl = coverUrl;
        Finished = finished;
        LastReadAt = lastReadAt;
        Badge = badge;
    }

    public static ShelfBookItem FromShelf(ShelfBook book) => new(
        book.BookId,
        readerId: null,
        book.Title,
        string.IsNullOrEmpty(book.Author) ? "佚名" : book.Author,
        book.Cover,
        book.Finished,
        book.ReadUpdateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(book.ReadUpdateTime).ToLocalTime()
            : null,
        badge: string.Empty);

    public static ShelfBookItem FromHome(HomeBook book) => new(
        bookId: string.Empty,
        book.ReaderId,
        book.Title,
        string.IsNullOrEmpty(book.Author) ? "佚名" : book.Author,
        book.Cover,
        finished: false,
        lastReadAt: null,
        badge: "可试看");

    public string BookId { get; }

    /// <summary>未登录时只有官网的 readerId，没有登录态下的 bookId。</summary>
    public string? ReaderId { get; }

    public string Title { get; }

    public string Author { get; }

    public string CoverUrl { get; }

    public bool Finished { get; }

    public DateTimeOffset? LastReadAt { get; }

    /// <summary>封面左下角角标，未登录时显示「可试看」。</summary>
    public string Badge { get; }

    public ImageSource? Cover { get; private set; }

    public string ProgressLabel => Finished
        ? "已读完"
        : LastReadAt is { } time ? $"上次 {time:MM-dd}" : string.Empty;

    public Visibility ProgressVisibility => string.IsNullOrEmpty(ProgressLabel) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;

    public void LoadCover()
    {
        if (Cover is not null || string.IsNullOrEmpty(CoverUrl))
        {
            return;
        }

        if (Uri.TryCreate(CoverUrl, UriKind.Absolute, out Uri? uri))
        {
            Cover = new BitmapImage(uri);
        }
    }
}

/// <summary>
/// 书架页：已登录时读取 /shelf/sync 展示书架；未登录时展示官网首页推荐的前几本书，
/// 点进去只能试看前几章，右上角提供登录入口。
/// </summary>
public sealed partial class ShelfPage : Page
{
    private const int HomeBookCount = 4;

    /// <summary>封面卡片（含标题作者）的宽度，与 ShelfPage.xaml 里 DataTemplate 的 Width 保持一致。</summary>
    private const double CardWidth = 132;

    /// <summary>书与书之间、以及书架左右留白的最小值。</summary>
    private const double MinCardGap = 24;

    /// <summary>GridView 左右各留的空白，与 ShelfPage.xaml 里的 Padding 保持一致。</summary>
    private const double ListHorizontalPadding = 24;

    /// <summary>折叠阈值的兜底值；首次测量到标题栏自然宽度后会用它替换。</summary>
    private const double CompactHeaderWidth = 760;

    /// <summary>测量自然宽度时预留的余量，避免正好卡在边界上来回切换。</summary>
    private const double HeaderFitTolerance = 24;

    /// <summary>未登录时最多能读到第几章（含版权页等前置页）。</summary>
    public const int GuestChapterLimit = 3;

    private readonly ObservableCollection<ShelfBookItem> _books = [];
    private CancellationTokenSource _lifetime = new();
    private bool _loaded;

    /// <summary>上次加载数据时的登录状态，用来判断返回本页后是否需要重新加载。</summary>
    private bool _loadedForSignedIn;

    /// <summary>书架网格的布局面板，用于按窗口宽度重算列宽。</summary>
    private ItemsWrapGrid? _booksPanel;

    /// <summary>标题栏当前是否处于折叠态。ApplyHeaderLayout 每次都按此值与登录状态重算可见性。</summary>
    private bool _headerCompact;

    /// <summary>宽屏标题栏排满一行所需的宽度，实测得到，用于决定何时折叠。</summary>
    private double _headerNaturalWidth;

    /// <summary>没有书目可显示时的提示，由当前数据来源（书架 / 推荐）决定。</summary>
    private string _emptyMessage = "书架是空的。";

    /// <summary>是否正在加载，避免刷新按钮与导航返回同时触发两次请求。</summary>
    private bool _loading;

    public ShelfPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _lifetime.Cancel();
    }

    public ObservableCollection<ShelfBookItem> FilteredBooks { get; } = [];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Frame 会复用页面实例（例如从登录页返回、从阅读页返回），
        // 上次离开时取消的令牌必须换新的，否则刷新/加载会静默失败。
        if (_lifetime.IsCancellationRequested)
        {
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
        }

        // 从登录页返回时会重用本页实例，登录状态可能已经变了。
        ApplySignInState();

        if (!_loaded)
        {
            _loaded = true;
            _ = LoadAsync();
            return;
        }

        // 登录状态变了（登录成功或已退出）就重新拉一次数据，否则书架上还是上一个身份的列表。
        if (_loadedForSignedIn != App.Session.IsSignedIn)
        {
            _ = LoadAsync();
        }
    }

    /// <summary>登录状态变化后刷新标题文案与按钮组合，折叠与否交给 ApplyHeaderLayout。</summary>
    private void ApplySignInState()
    {
        bool signedIn = App.Session.IsSignedIn;

        TitleText.Text = signedIn ? "我的书架" : "发现好书";
        GuestHint.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;

        ApplyHeaderLayout(_headerCompact);

        if (!signedIn)
        {
            // 程序化清空 Text 触发的是 ProgrammaticChange 变更，OnSearchTextChanged 会直接返回，
            // 必须在这里补一次过滤，否则搜索框空了、列表却还停在旧关键词的结果上。
            SearchBox.Text = string.Empty;
            CompactSearchBox.Text = string.Empty;
            ApplyFilter(keyword: null);
        }
        else if (!_headerCompact)
        {
            // 登录态切换会改变搜索框和登录/退出按钮的宽度（退出登录比登录宽），
            // 等这一轮布局结束再量一次，避免刚登录时按钮挤在一起。
            DispatcherQueue.TryEnqueue(() => UpdateHeaderLayout(HeaderHost.ActualWidth));
        }
    }

    /// <summary>
    /// 窗口变窄时收缩标题栏：搜索框收成放大镜图标（点击弹出输入框），
    /// 刷新与登录/退出一起收进 ☰，与阅读页的折叠方式保持一致。
    /// </summary>
    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeaderLayout(e.NewSize.Width);

    private void UpdateHeaderLayout(double width)
    {
        // 平铺时量一次自然宽度（标题 + 搜索框 + 按钮 + 间距 + 左右 padding），
        // 之后窗口比它窄就折叠。取最大值，避免登录状态切换时算出的宽度互相覆盖。
        if (!_headerCompact && HeaderActions.ActualWidth > 0)
        {
            double natural = HeaderHost.Padding.Left + HeaderHost.Padding.Right
                + TitleText.ActualWidth
                + SearchBox.ActualWidth
                + HeaderActions.ActualWidth
                + (HeaderHost.ColumnSpacing * 3);
            if (natural > _headerNaturalWidth)
            {
                _headerNaturalWidth = natural;
            }
        }

        // 自然宽度还没测出来时（首次布局）先用兜底值。
        double threshold = _headerNaturalWidth > 0
            ? _headerNaturalWidth + HeaderFitTolerance
            : CompactHeaderWidth;
        ApplyHeaderLayout(width > 0 && width < threshold);
    }

    /// <summary>
    /// 按折叠态排布标题栏：宽屏平铺搜索框与刷新/登录（退出），窄屏只留放大镜图标和 ☰。
    /// 登录与退出始终二选一，折叠与否都按同一规则显示。
    /// </summary>
    private void ApplyHeaderLayout(bool compact)
    {
        bool signedIn = App.Session.IsSignedIn;
        _headerCompact = compact;

        SearchBox.Visibility = !compact && signedIn ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.Visibility = compact && signedIn ? Visibility.Visible : Visibility.Collapsed;

        // ☰ 里的菜单项与这块平铺区一一对应，折叠时整块隐藏。
        HeaderActions.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        MenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        if (!compact)
        {
            if (ActionsFlyout.IsOpen)
            {
                ActionsFlyout.Hide();
            }

            if (SearchFlyout.IsOpen)
            {
                SearchFlyout.Hide();
            }
        }
    }

    private void OnSearchButtonClick(object sender, RoutedEventArgs e)
    {
        // 浮层里的输入框与顶部搜索框同步，切换折叠态时关键词不会丢。
        CompactSearchBox.Text = SearchBox.Text;
    }

    private void OnSearchFlyoutOpening(object sender, object e)
    {
        CompactSearchBox.Text = SearchBox.Text;

        // 浮层的内容要等这一轮布局结束才可聚焦，直接 Focus 常常没有效果。
        DispatcherQueue.TryEnqueue(() => CompactSearchBox.Focus(FocusState.Programmatic));
    }

    private void OnCompactSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        SearchBox.Text = sender.Text;
        ApplyFilter(sender.Text);
    }

    /// <summary>浮层里按回车即完成搜索，不必再点一下关闭。</summary>
    private void OnCompactSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        => SearchFlyout.Hide();

    /// <summary>☰ 的内容按当前登录状态重建，与标题栏平铺时显示的那组按钮保持一致。</summary>
    private void OnActionsFlyoutOpening(object sender, object e)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 160 };

        panel.Children.Add(FlyoutMenu.BuildRow(ActionsFlyout, "刷新", OnRefreshClick, !_loading, "重新加载书目"));

        // 登录与退出二选一，规则和 HeaderActions 里的按钮完全一致。
        string label = App.Session.IsSignedIn ? "退出登录" : "登录";
        RoutedEventHandler handler = App.Session.IsSignedIn ? OnSignOutClick : OnSignInClick;
        panel.Children.Add(FlyoutMenu.BuildRow(ActionsFlyout, label, handler, isEnabled: true, label));

        ActionsFlyout.Content = panel;
    }

    private void OnBooksViewLoaded(object sender, RoutedEventArgs e)
    {
        // ItemsPanel 要等模板展开后才创建，这里算一次列宽（面板引用由 UpdateBookCells 缓存）。
        UpdateBookCells(BooksView.ActualWidth);
    }

    private void OnBooksViewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBookCells(e.NewSize.Width);

    /// <summary>
    /// 按可用宽度重算格宽，让一行正好铺满。
    /// GridView 默认按首张卡片的固定宽度定列数，一行铺不满的余量会全堆在行尾，
    /// 视觉上就是右侧留白比左侧宽出一大块。这里改成 N 格等宽铺满可用宽度、
    /// 卡片在格内居中，格子多出来的部分左右各一半，两侧留白因此始终相等。
    /// </summary>
    private void UpdateBookCells(double listWidth)
    {
        _booksPanel ??= BooksView.ItemsPanelRoot as ItemsWrapGrid;
        if (_booksPanel is null || listWidth <= 0)
        {
            return;
        }

        double usable = listWidth - (ListHorizontalPadding * 2);
        if (usable < CardWidth)
        {
            return;
        }

        // 每格至少要能放开一张卡片和一段最小间距，否则会把书挤扁。
        int columns = Math.Max(1, (int)((usable + MinCardGap) / (CardWidth + MinCardGap)));
        _booksPanel.ItemWidth = usable / columns;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OnSignInClick(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(LoginPage));

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "退出登录",
            Content = "将清除本机保存的登录凭证，需要重新扫码才能继续阅读。",
            PrimaryButtonText = "退出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await App.Session.SignOutAsync();
            await LoadAsync();
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        CompactSearchBox.Text = sender.Text;
        ApplyFilter(sender.Text);
    }

    private void ApplyFilter(string? keyword)
    {
        FilteredBooks.Clear();
        IEnumerable<ShelfBookItem> source = string.IsNullOrWhiteSpace(keyword)
            ? _books
            : _books.Where(b => b.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || b.Author.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        foreach (ShelfBookItem item in source)
        {
            item.LoadCover();
            FilteredBooks.Add(item);
        }

        // 搜索没有命中时提示，比一律显示“书架是空的”更贴近实际情况。
        EmptyText.Text = FilteredBooks.Count == 0 && !string.IsNullOrWhiteSpace(keyword)
            ? "没有匹配的书。"
            : _emptyMessage;
        EmptyText.Visibility = FilteredBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>加载期间的公共 UI 状态：转圈、清错误条、禁用刷新。</summary>
    private void BeginLoading()
    {
        LoadingRing.IsActive = true;
        ErrorBar.IsOpen = false;
        RefreshButton.IsEnabled = false;
    }

    private void EndLoading()
    {
        LoadingRing.IsActive = false;
        RefreshButton.IsEnabled = true;
    }

    private async Task LoadAsync()
    {
        // 导航返回与刷新按钮可能同时触发，重入会让 LoadingRing 与 IsEnabled 互相抵消。
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            ApplySignInState();
            _loadedForSignedIn = App.Session.IsSignedIn;

            if (_loadedForSignedIn)
            {
                await LoadShelfAsync();
            }
            else
            {
                await LoadHomeAsync();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>未登录：抓官网首页的前四本推荐书。</summary>
    private async Task LoadHomeAsync()
    {
        BeginLoading();
        EmptyText.Text = "正在加载推荐书目…";
        EmptyText.Visibility = Visibility.Visible;

        try
        {
            IReadOnlyList<HomeBook> home = await App.Guest.GetHomeBooksAsync(HomeBookCount, _lifetime.Token);
            _books.Clear();
            foreach (HomeBook book in home)
            {
                _books.Add(ShelfBookItem.FromHome(book));
            }

            _emptyMessage = "暂时没有推荐书目。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError($"加载推荐书目失败：{ex.Message}");
            _emptyMessage = "暂时没有拿到推荐书目，请稍后重试。";
        }
        finally
        {
            ApplyFilter(keyword: null);
            EndLoading();
        }
    }

    private async Task LoadShelfAsync()
    {
        BeginLoading();

        try
        {
            IReadOnlyList<ShelfBook> shelf = await App.Session.Mobile.GetShelfAsync(_lifetime.Token);
            _books.Clear();
            foreach (ShelfBook book in shelf)
            {
                _books.Add(ShelfBookItem.FromShelf(book));
            }

            _emptyMessage = "书架是空的。";
        }
        catch (WeReadAuthException)
        {
            // 凭证失效时不硬跳登录页：退回访客首页，由用户自己决定是否重新登录。
            await App.Session.SignOutAsync();
            _loadedForSignedIn = false;
            ApplySignInState();
            await LoadHomeAsync();
            ShowError("登录已失效，已切回未登录浏览。");

            // 书架为空且已回落到访客首页，列表与加载态都已由 LoadHomeAsync 处理。
            return;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError($"读取书架失败：{ex.Message}");
        }

        ApplyFilter(SearchBox.Text);
        EndLoading();
    }

    private void OnBookClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ShelfBookItem item)
        {
            return;
        }

        if (App.Session.IsSignedIn && item.BookId.Length > 0)
        {
            Frame.Navigate(typeof(ReaderPage), item.BookId);
            return;
        }

        if (item.ReaderId is { Length: > 0 } readerId)
        {
            // 访客模式：阅读页按官网 readerId 打开，只开放前几章。
            Frame.Navigate(typeof(ReaderPage), readerId);
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
