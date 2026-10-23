namespace Launcher.Application.Diagnostics;

/// <summary>
/// 把嵌套异常压成一句人话。下载器会把一批文件的失败包成
/// <see cref="AggregateException"/>，直接 <c>ex.Message</c> 只会显示「12/84 个文件下载失败」，
/// 真正的原因（停滞 / sha1 不符 / 404）都在 InnerExceptions 里。
/// </summary>
public static class ErrorText
{
    public static string Describe(Exception exception) => exception switch
    {
        AggregateException aggregate => string.Join(
            "；",
            aggregate.Flatten().InnerExceptions.Select(Describe)),

        IOException { InnerException: { } inner } io when !string.IsNullOrWhiteSpace(inner.Message) =>
            $"{io.Message}（{inner.Message}）",

        HttpRequestException { InnerException: { } inner } http when !string.IsNullOrWhiteSpace(inner.Message) =>
            $"{http.Message}（{inner.Message}）",

        _ => exception.Message,
    };
}
