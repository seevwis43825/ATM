// ===== 容量压测（Load Test）=====
// 回答「系统能承担多少」——阶梯加压，测出 QPS、P50/P95/P99、错误率拐点。
// 与 StressTest 的区别：StressTest 验证正确性，本测试验证容量。

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var hostBase = args.Length > 0 ? args[0] : "http://localhost:5243";
var bankBase = args.Length > 1 ? args[1] : "http://localhost:5200";

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 512,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
};
var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

// 获取令牌
var loginContent = new StringContent(
    """{"userId":"u_demo01","password":"demo1234"}""", Encoding.UTF8, "application/json");
var loginResp = await http.PostAsync($"{hostBase}/api/auth/token", loginContent);
if (!loginResp.IsSuccessStatusCode)
{
    Console.WriteLine("[FATAL] 无法获取令牌，退出");
    return 1;
}
using var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
var token = loginDoc.RootElement.GetProperty("token").GetString()!;

// 中文消息（用 \u 转义避免编码问题）
const string MsgCards = "\u6211\u6709\u54ea\u4e9b\u5361";
const string MsgBill = "\u6211\u8fd9\u4e2a\u6708\u82b1\u4e86\u591a\u5c11\u94b1";

Console.WriteLine(new string('=', 78));
Console.WriteLine(" AI Banking Agent 容量压测");
Console.WriteLine(new string('=', 78));
Console.WriteLine($"目标: {hostBase}");
Console.WriteLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine();

// 预热，避免 JIT 编译计入结果
Console.WriteLine("预热中（20 次）...");
for (var i = 0; i < 20; i++)
{
    await SendChatAsync(token, MsgCards, i);
}
Console.WriteLine("预热完成");
Console.WriteLine();

// ===== 阶梯加压 =====
var stages = new[]
{
    (Concurrency: 5,   Duration: 10, Label: "5 并发"),
    (Concurrency: 10,  Duration: 15, Label: "10 并发"),
    (Concurrency: 20,  Duration: 15, Label: "20 并发"),
    (Concurrency: 40,  Duration: 20, Label: "40 并发"),
    (Concurrency: 80,  Duration: 20, Label: "80 并发"),
    (Concurrency: 120, Duration: 20, Label: "120 并发"),
    (Concurrency: 200, Duration: 20, Label: "200 并发"),
};

Console.WriteLine("| 并发 | 请求数 | 成功率 | QPS | P50 | P95 | P99 | 最大延迟 |");
Console.WriteLine("|------|-------|--------|-----|-----|-----|-----|---------|");

var results = new List<(int Concurrency, double Qps, double P95, double ErrorRate)>();
// 累积全部阶段的样本，供末尾的失败归因使用（各阶段单独统计会丢失全局分布）
var allSamples = new List<Sample>();

// 把异常压成一行可读的归因标签。
// 超时 / 连接被拒 / 服务端 5xx 三种情况的结论完全不同，必须区分。
static string DescribeError(Exception ex) => ex switch
{
    TaskCanceledException or OperationCanceledException => "TIMEOUT",
    HttpRequestException http => $"HTTP:{http.StatusCode?.ToString() ?? http.InnerException?.GetType().Name ?? "CONNECT"}",
    _ => ex.GetType().Name
};

foreach (var (concurrency, duration, label) in stages)
{
    var samples = await RunStageAsync(token, concurrency, duration, MsgCards, MsgBill);
    allSamples.AddRange(samples);
    var wall = duration;

    var ok = samples.Count(s => s.Status == 200);
    var successRate = ok * 100.0 / samples.Count;
    var qps = samples.Count / (double)wall;

    var sorted = samples.Select(s => s.Ms).OrderBy(x => x).ToList();
    double Percentile(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
    var p50 = Percentile(0.50);
    var p95 = Percentile(0.95);
    var p99 = Percentile(0.99);
    var max = sorted.Count == 0 ? 0 : sorted[^1];
    var errRate = 100 - successRate;

    Console.WriteLine(
        $"| {label,-4} | {samples.Count,5} | {successRate,6:F1}% | {qps,5:F1} | " +
        $"{p50,4:F0}ms | {p95,4:F0}ms | {p99,4:F0}ms | {max,6:F0}ms |");

    results.Add((concurrency, qps, p95, errRate));

    // 错误率超过 5% 或 P95 超过 3 秒，认为已达容量上限，停止加压
    if (errRate > 5 || p95 > 3000)
    {
        Console.WriteLine($"\n>>> 容量拐点：{label} 时错误率或延迟越界，停止加压");
        break;
    }

    await Task.Delay(2000); // 阶段间冷却
}

// ===== 容量结论 =====
Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine(" 容量结论");
Console.WriteLine(new string('=', 78));

var best = results.OrderByDescending(r => r.Qps).First();
var stable = results.Where(r => r.ErrorRate <= 1).OrderByDescending(r => r.Concurrency).FirstOrDefault();

Console.WriteLine($"  峰值吞吐        : {best.Qps:F1} QPS（{best.Concurrency} 并发）");
if (stable.Concurrency > 0)
{
    Console.WriteLine($"  稳定承载        : {stable.Concurrency} 并发 @ {stable.Qps:F1} QPS（P95 {stable.P95:F0}ms，错误率 {stable.ErrorRate:F2}%）");
}
else
{
    Console.WriteLine($"  稳定承载        : 未能建立稳定区间");
}

Console.WriteLine();
Console.WriteLine(" SLA 换算（按稳态 QPS 的 60% 作为安全运行水位）");
var safeQps = best.Qps * 0.6;
Console.WriteLine($"  安全运行水位    : {safeQps:F0} QPS");
Console.WriteLine($"  日处理能力      : {safeQps * 86400:N0} 请求/天（单实例）");
Console.WriteLine($"  峰值日处理      : {safeQps * 86400 * 0.15:N0} 请求/天（按 15% 峰值系数）");

Console.WriteLine();
Console.WriteLine(" 资源观察");
Console.WriteLine($"  进程工作集      : {Environment.WorkingSet / 1024 / 1024:F0} MB（压测进程）");

// 失败归因：只报错误率不足以定位瓶颈，必须给出失败类型分布
Console.WriteLine();
Console.WriteLine(" 失败归因（全部阶段合计）");
var failedTotal = allSamples.Count(s => s.Status != 200);
if (failedTotal == 0)
{
    Console.WriteLine("  无失败请求");
}
else
{
    var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var s in allSamples)
    {
        if (s.Status == 200) continue;
        var key = s.Status == -1 ? s.Error ?? "UNKNOWN" : $"HTTP {s.Status}";
        byReason[key] = byReason.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    foreach (var kv in byReason.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(8))
    {
        Console.WriteLine(
            $"  {kv.Key,-22} {kv.Value,7} 次  ({kv.Value * 100.0 / allSamples.Count:F2}%)");
    }
}

return 0;

// ===== 压测阶段 =====
async Task<List<Sample>> RunStageAsync(
    string bearer, int concurrency, int seconds, string msg1, string msg2)
{
    var samples = new System.Collections.Concurrent.ConcurrentBag<Sample>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    var counter = 0;

    var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
    {
        while (!cts.IsCancellationRequested)
        {
            var idx = Interlocked.Increment(ref counter);
            var msg = idx % 2 == 0 ? msg1 : msg2;
            var sw = Stopwatch.StartNew();
            try
            {
                var resp = await SendChatAsync(bearer, msg, idx);
                sw.Stop();
                samples.Add(new Sample((int)resp.StatusCode, sw.Elapsed.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                sw.Stop();
                // 异常不能只记成 -1：连接被拒、超时、限流三种情况的结论完全不同，
                // 丢掉异常类型会让压测报告无法归因。
                samples.Add(new Sample(-1, sw.Elapsed.TotalMilliseconds, DescribeError(ex)));
            }
        }
    });

    await Task.WhenAll(tasks);
    return samples.ToList();
}

async Task<HttpResponseMessage> SendChatAsync(string bearer, string message, int seq)
{
    // 转账场景不走，会产生副作用；这里用只读场景测容量
    var body = $@"{{""message"":""{message}"",""userId"":""u_demo01"",""sessionId"":""load-{seq}-{Guid.NewGuid():N}""}}";
    var req = new HttpRequestMessage(HttpMethod.Post, $"{hostBase}/api/chat")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    return await http.SendAsync(req);
}

/// <summary>单次请求结果。</summary>
record Sample(int Status, double Ms, string? Error = null);
