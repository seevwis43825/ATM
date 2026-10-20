// ===== 模拟银行配置 =====

namespace MockBank.Api.Data;

/// <summary>模拟银行系统的可配置项。</summary>
public sealed class MockBankOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "MockBank";

    /// <summary>
    /// 各行数据库文件所在目录（相对路径按宿主内容根目录解析）。
    ///
    /// 默认值刻意用 <c>App_Data</c> 而不是 <c>data</c>：
    /// Windows 文件系统大小写不敏感，<c>data</c> 会直接命中源码目录 <c>Data/</c>，
    /// 于是运行期数据库文件被写进源码树；而 Linux 上 <c>data</c> 与 <c>Data</c> 是两个目录，
    /// 同一份代码在两端落到不同位置，排查时极难对上。
    /// <c>App_Data</c> 在两端行为一致，且语义上就代表"运行期数据而非源码"。
    /// </summary>
    public string DataDirectory { get; set; } = "App_Data";

    /// <summary>
    /// 启动时是否重建全部银行数据库。
    /// 默认 false —— 数据在重启后保留，更像真实核心系统；
    /// 需要干净演示数据时，删除 App_Data 目录下的 .db 文件或临时置为 true。
    /// </summary>
    public bool ResetOnStartup { get; set; }

    /// <summary>启用哪些银行（行代码）。为空表示启用目录中的全部银行。</summary>
    public IList<string> Banks { get; set; } = [];
}
