namespace xinread.WeRead;

/// <summary>微信读书接口返回的业务错误（HTTP 200 携带负数 errCode，或非 2xx 响应）。</summary>
public class WeReadException : Exception
{
    public WeReadException(string message, string? path = null, int? status = null, int? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Path = path;
        Status = status;
        ErrorCode = errorCode;
    }

    public string? Path { get; }

    public int? Status { get; }

    public int? ErrorCode { get; }
}

/// <summary>凭证缺失、过期或被拒绝，需要重新扫码登录。</summary>
public sealed class WeReadAuthException : WeReadException
{
    public WeReadAuthException(string message, string? path = null, Exception? inner = null)
        : base(message, path, null, null, inner)
    {
    }
}

/// <summary>章节正文在传输、校验或解码环节不合法。</summary>
public sealed class ChapterFormatException : WeReadException
{
    public ChapterFormatException(string message, string? path = "/web/book/chapter", Exception? inner = null)
        : base(message, path, null, null, inner)
    {
    }
}
