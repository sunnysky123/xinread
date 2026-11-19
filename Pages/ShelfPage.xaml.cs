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

    /// <summary>未登录时最多能读到第几章（含版权页等前置页）。</summary>
    public const int GuestChapterLimit = 3;

    private readonly ObservableCollection<ShelfBookItem> _books = [];
    private CancellationTokenSource _lifetime = new();
    private bool _loaded;

    /// <summary>上次加载数据时的登录状态，用来判断返回本页后是否需要重新加载。</summary>
    private bool _loadedForSignedIn;

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

    /// <summary>未登录时隐藏刷新/退出/搜索，显示登录按钮；标题也跟着换。</summary>
    private void ApplySignInState()
    {
        bool signedIn = App.Session.IsSignedIn;

        TitleText.Text = signedIn ? "我的书架" : "发现好书";
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        GuestHint.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
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
        if (FilteredBooks.Count == 0 && !string.IsNullOrWhiteSpace(keyword))
        {
            EmptyText.Text = "没有匹配的书。";
        }

        EmptyText.Visibility = FilteredBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadAsync()
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

    /// <summary>未登录：抓官网首页的前四本推荐书。</summary>
    private async Task LoadHomeAsync()
    {
        LoadingRing.IsActive = true;
        ErrorBar.IsOpen = false;
        RefreshButton.IsEnabled = false;
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

            ApplyFilter(keyword: null);
            EmptyText.Text = "暂时没有拿到推荐书目，请稍后重试。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError($"加载推荐书目失败：{ex.Message}");
            ApplyFilter(keyword: null);
            EmptyText.Text = "暂时没有拿到推荐书目，请稍后重试。";
        }
        finally
        {
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }
    private async Task LoadShelfAsync()
    {
        LoadingRing.IsActive = true;
        ErrorBar.IsOpen = false;
        RefreshButton.IsEnabled = false;

        try
        {
            IReadOnlyList<ShelfBook> shelf = await App.Session.Mobile.GetShelfAsync(_lifetime.Token);
            _books.Clear();
            foreach (ShelfBook book in shelf)
            {
                _books.Add(ShelfBookItem.FromShelf(book));
            }

            ApplyFilter(SearchBox.Text);
            EmptyText.Text = "书架是空的。";
        }
        catch (WeReadAuthException)
        {
            // 凭证失效时不硬跳登录页：退回访客首页，由用户自己决定是否重新登录。
            await App.Session.SignOutAsync();
            _loadedForSignedIn = false;
            ApplySignInState();
            await LoadHomeAsync();
            ShowError("登录已失效，已切回未登录浏览。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError($"读取书架失败：{ex.Message}");
            ApplyFilter(SearchBox.Text);
        }
        finally
        {
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
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
