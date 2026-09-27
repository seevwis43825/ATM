// ===== 业务日志写入器：把转账 / 认购等关键业务动作追加到 logs/mockbank-requests.log =====

using System.Text;

namespace MockBank.Api.Services;

/// <summary>
/// 业务日志写入器。以文本追加方式写入 <c>logs/mockbank-requests.log</c>，
/// 每行格式为：<c>时间 | userId | operation | amount | result</c>。
/// </summary>
public sealed class BusinessLogWriter
{
    private const string LogDirectoryName = "logs";
    private const string LogFileName = "mockbank-requests.log";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _logFilePath;
    private readonly ILogger<BusinessLogWriter> _logger;
    private readonly object _sync = new();

    /// <summary>创建日志写入器，并确保日志目录存在。</summary>
    /// <param name="environment">宿主环境，用于定位内容根目录。</param>
    /// <param name="logger">日志记录器。</param>
    public BusinessLogWriter(IHostEnvironment environment, ILogger<BusinessLogWriter> logger)
    {
        _logger = logger;
        var directory = Path.Combine(environment.ContentRootPath, LogDirectoryName);
        Directory.CreateDirectory(directory);
        _logFilePath = Path.Combine(directory, LogFileName);
    }

    /// <summary>业务日志文件的完整路径。</summary>
    public string LogFilePath => _logFilePath;

    /// <summary>追加一条业务日志。</summary>
    /// <param name="userId">客户号，未知时传 "-"。</param>
    /// <param name="operation">业务操作名称，如 TRANSFER / WEALTH_SUBSCRIBE。</param>
    /// <param name="amount">涉及金额。</param>
    /// <param name="result">执行结果，如 SUCCESS:TX2026... 或 FAILED:INSUFFICIENT_FUNDS。</param>
    public void Write(string? userId, string operation, decimal amount, string result)
    {
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} | {OrDash(userId)} | {operation} | {amount:F2} | {result}");

        try
        {
            lock (_sync)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine, Utf8NoBom);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "写入业务日志失败：{LogFile}", _logFilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "写入业务日志被拒绝：{LogFile}", _logFilePath);
        }
    }

    private static string OrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
