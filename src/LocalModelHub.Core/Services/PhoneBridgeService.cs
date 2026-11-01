using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>
/// 手机消息桥：在本机监听局域网端口，给手机提供扫码即用的收发页面，
/// 同时暴露通用入站 Webhook，供 OpenClaw / 企业微信转发消息到电脑。
/// 只保存最近 <see cref="MaxMessages"/> 条消息，重启应用后清空。
/// </summary>
public sealed class PhoneBridgeService : IDisposable
{
    public const int DefaultPort = 18790;
    public const int MaxMessages = 200;
    public const int MaxTextLength = 4000;
    private const long MaxBodyBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _sync = new();
    private readonly List<BridgeMessage> _messages = [];
    private readonly List<HttpListener> _listeners = [];
    private readonly List<CancellationTokenSource> _cancellations = [];
    private long _sequence;
    private bool _enabled;
    private int _port = DefaultPort;
    private string _token = "";

    /// <summary>收到手机 / Webhook 发来的消息（已切回调用线程，UI 侧需再切到 UI 线程）。</summary>
    public event EventHandler<BridgeMessage>? MessageAppended;

    /// <summary>桥的启停或异常状态变化。</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning { get; private set; }

    /// <summary>只监听到回环地址时为 true：手机无法从局域网连入，需要在页面提示。</summary>
    public bool LocalOnly { get; private set; }

    /// <summary>最近一次启动失败原因；启动成功时为空。</summary>
    public string? LastError { get; private set; }

    public bool Enabled
    {
        get { lock (_sync) return _enabled; }
    }

    public int Port
    {
        get { lock (_sync) return _port; }
    }

    public string Token
    {
        get { lock (_sync) return _token; }
    }

    public string LocalAddress => $"http://127.0.0.1:{Port}";

    public string LanAddress => $"http://{NetworkHelper.GetLanAddress()}:{Port}";

    /// <summary>手机扫码后打开的地址；Token 即配对凭证。</summary>
    public string PairUrl => $"{LanAddress}/?token={Uri.EscapeDataString(Token)}";

    /// <summary>给 OpenClaw / 企业微信等外部系统转发消息用的入站地址。</summary>
    public string InboundWebhookUrl => $"{LanAddress}/api/inbound?token={Uri.EscapeDataString(Token)}";

    /// <summary>16 位十六进制配对码；每次重置后旧链接立即失效。</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>读取当前消息快照；Sequence 大于 after 的消息按时间顺序返回。</summary>
    public IReadOnlyList<BridgeMessage> MessagesSince(long after = 0, int limit = 50)
    {
        lock (_sync)
        {
            limit = Math.Clamp(limit, 1, MaxMessages);
            return _messages.Where(message => message.Sequence > after).Take(limit).ToArray();
        }
    }

    public IReadOnlyList<BridgeMessage> Snapshot()
    {
        lock (_sync) return _messages.ToArray();
    }

    /// <summary>更新配置：启用时立即启动监听，关闭时停止；端口或配对码变化会重建监听器。</summary>
    public void Configure(bool enabled, int port, string token)
    {
        var normalizedPort = port is >= 1024 and <= 65535 ? port : DefaultPort;
        var normalizedToken = string.IsNullOrWhiteSpace(token) ? NewToken() : token.Trim();

        bool restart;
        lock (_sync)
        {
            restart = _port != normalizedPort || _token != normalizedToken;
            _enabled = enabled;
            _port = normalizedPort;
            _token = normalizedToken;
        }

        if (enabled)
        {
            if (restart || !IsRunning) Start();
        }
        else
        {
            Stop();
        }
    }

    public void Start()
    {
        Stop();
        LastError = null;

        int port;
        lock (_sync) port = _port;

        HttpListener? listener = null;
        string? error = null;
        var localOnly = false;

        // 0.0.0.0 覆盖所有网卡（含回环）；部分系统只授权回环前缀，退化为仅本机监听。
        foreach (var prefix in new[] { $"http://0.0.0.0:{port}/", $"http://127.0.0.1:{port}/" })
        {
            var candidate = new HttpListener();
            candidate.Prefixes.Add(prefix);
            try
            {
                candidate.Start();
                listener = candidate;
                localOnly = prefix.Contains("127.0.0.1", StringComparison.Ordinal);
                break;
            }
            catch (Exception ex)
            {
                candidate.Close();
                error = ex.Message;
            }
        }

        if (listener is null)
        {
            LastError = $"无法监听端口 {port}：{error}。请换一个端口，或确认防火墙允许局域网访问。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _listeners.Add(listener);
            _cancellations.Add(cancellation);
            LocalOnly = localOnly;
            IsRunning = true;
        }

        _ = Task.Run(() => AcceptLoopAsync(listener, cancellation.Token));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        HttpListener[] listeners;
        CancellationTokenSource[] cancellations;
        lock (_sync)
        {
            listeners = _listeners.ToArray();
            cancellations = _cancellations.ToArray();
            _listeners.Clear();
            _cancellations.Clear();
            IsRunning = false;
            LocalOnly = false;
        }

        foreach (var cancellation in cancellations)
        {
            try
            {
                // 只发取消信号，不立即 Dispose：接受循环可能仍在读取这个 token。
                cancellation.Cancel();
            }
            catch
            {
                // ignore
            }
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch
            {
                // 已关闭
            }
        }

        if (listeners.Length > 0) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearMessages()
    {
        lock (_sync) _messages.Clear();
    }

    /// <summary>电脑 → 手机：写入消息列表，移动端下次轮询即可看到。</summary>
    public BridgeMessage AppendOutbound(string text, string sender = "电脑") =>
        Append("out", text, sender, "phone");

    /// <summary>手机 / Webhook → 电脑：写入消息列表并触发 <see cref="MessageAppended"/>。</summary>
    public BridgeMessage AppendInbound(string text, string sender = "手机", string channel = "phone")
    {
        var message = Append("in", text, sender, channel);
        MessageAppended?.Invoke(this, message);
        return message;
    }

    private BridgeMessage Append(string direction, string text, string sender, string channel)
    {
        var cleaned = (text ?? "").Trim();
        if (cleaned.Length > MaxTextLength) cleaned = cleaned[..MaxTextLength];
        if (string.IsNullOrEmpty(cleaned)) throw new ArgumentException("消息内容不能为空", nameof(text));

        BridgeMessage message;
        lock (_sync)
        {
            message = new BridgeMessage(
                ++_sequence,
                direction,
                cleaned,
                string.IsNullOrWhiteSpace(sender) ? "手机" : sender.Trim(),
                string.IsNullOrWhiteSpace(channel) ? "phone" : channel.Trim(),
                DateTimeOffset.Now);
            _messages.Add(message);
            while (_messages.Count > MaxMessages) _messages.RemoveAt(0);
        }

        if (direction == "out") MessageAppended?.Invoke(this, message);
        return message;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');

            if (request.HttpMethod == "GET" && path.Length == 0)
            {
                await WriteHtmlAsync(context, MobilePage);
                return;
            }

            if (!IsAuthorized(request))
            {
                await WriteJsonAsync(context, 401, new { ok = false, error = "配对码无效" });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/ping")
            {
                await WriteJsonAsync(context, 200, new
                {
                    ok = true,
                    serverTime = DateTimeOffset.Now,
                    messages = 0
                });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/messages")
            {
                var after = ParseLong(request.QueryString["after"]);
                var messages = MessagesSince(after);
                await WriteJsonAsync(context, 200, new
                {
                    ok = true,
                    serverTime = DateTimeOffset.Now,
                    latest = messages.Count == 0 ? after : messages[^1].Sequence,
                    messages
                });
                return;
            }

            if (request.HttpMethod == "POST" && path is "/api/messages" or "/api/inbound")
            {
                var payload = await ReadPayloadAsync(request, context);
                if (payload is null) return;

                var message = AppendInbound(payload.Value.Text, payload.Value.Sender, payload.Value.Channel);
                await WriteJsonAsync(context, 200, new { ok = true, message });
                return;
            }

            await WriteJsonAsync(context, 404, new { ok = false, error = "接口不存在" });
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(context, 500, new { ok = false, error = ex.Message });
            }
            catch
            {
                // 连接已断开
            }
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var provided = request.QueryString["token"];
        if (string.IsNullOrEmpty(provided))
        {
            var header = request.Headers["Authorization"] ?? "";
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                provided = header["Bearer ".Length..].Trim();
            }
        }

        string expected;
        lock (_sync) expected = _token;
        return FixedTimeEquals(expected, provided);
    }

    private static bool FixedTimeEquals(string expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)) return false;
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(provided);
        return left.Length == right.Length &&
               CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;

    private async Task<(string Text, string Sender, string Channel)?> ReadPayloadAsync(
        HttpListenerRequest request,
        HttpListenerContext context)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            await WriteJsonAsync(context, 413, new { ok = false, error = "消息过大" });
            return null;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await request.InputStream.ReadAsync(buffer.AsMemory(read), CancellationToken.None);
            if (chunk == 0) break;
            read += chunk;
        }

        if (read > MaxBodyBytes)
        {
            await WriteJsonAsync(context, 413, new { ok = false, error = "消息过大" });
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, read));
            var root = document.RootElement;
            var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(text))
            {
                await WriteJsonAsync(context, 400, new { ok = false, error = "缺少 text 字段" });
                return null;
            }

            var sender = root.TryGetProperty("sender", out var senderElement) && senderElement.ValueKind == JsonValueKind.String
                ? senderElement.GetString() ?? "手机"
                : "手机";
            var channel = root.TryGetProperty("channel", out var channelElement) && channelElement.ValueKind == JsonValueKind.String
                ? channelElement.GetString() ?? "phone"
                : "phone";
            return (text, sender, channel);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context, 400, new { ok = false, error = "请求体不是合法 JSON" });
            return null;
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose() => Stop();

    /// <summary>手机端单页应用：不依赖外部 CDN，按 2 秒轮询增量消息。</summary>
    private const string MobilePage = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>星坞 · 手机消息</title>
<style>
:root{--bg:#0b1020;--card:#151b31;--line:#26304f;--text:#e8ecff;--muted:#8c96b8;--accent:#6d8cff;--accent2:#41d6c3}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0;min-height:100vh;background:radial-gradient(1200px 600px at 15% -10%,#1d2b66 0%,transparent 60%),radial-gradient(900px 500px at 110% 110%,#0d3b3a 0%,transparent 55%),var(--bg);color:var(--text);font:15px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;display:flex;flex-direction:column}
header{padding:calc(14px + env(safe-area-inset-top)) 16px 10px;display:flex;align-items:center;gap:10px;border-bottom:1px solid var(--line);background:rgba(11,16,32,.72);backdrop-filter:blur(14px);position:sticky;top:0;z-index:2}
.logo{width:34px;height:34px;border-radius:11px;background:linear-gradient(135deg,var(--accent),var(--accent2));display:grid;place-items:center;font-weight:700}
.title{font-weight:600}.sub{font-size:12px;color:var(--muted)}
.dot{width:8px;height:8px;border-radius:50%;background:#556;margin-left:auto}
.dot.on{background:#3ddc97;box-shadow:0 0 10px #3ddc97}
#chat{flex:1;overflow-y:auto;padding:16px 14px 120px;display:flex;flex-direction:column;gap:10px}
.msg{max-width:82%;padding:10px 13px;border-radius:16px;word-break:break-word;white-space:pre-wrap;box-shadow:0 6px 18px rgba(0,0,0,.16)}
.msg.in{align-self:flex-start;background:var(--card);border:1px solid var(--line);border-bottom-left-radius:5px}
.msg.out{align-self:flex-end;background:linear-gradient(135deg,#3c62e8,#6d8cff);border-bottom-right-radius:5px}
.meta{font-size:11px;color:var(--muted);margin-top:5px}
footer{position:fixed;left:0;right:0;bottom:0;padding:10px 12px calc(10px + env(safe-area-inset-bottom));background:rgba(11,16,32,.86);border-top:1px solid var(--line);backdrop-filter:blur(14px);display:flex;gap:8px;align-items:flex-end}
textarea{flex:1;resize:none;height:44px;max-height:120px;padding:11px 13px;border-radius:13px;border:1px solid var(--line);background:var(--card);color:var(--text);font:inherit;outline:none}
button{width:auto;padding:0 18px;height:44px;border:0;border-radius:13px;background:linear-gradient(135deg,#3c62e8,#6d8cff);color:#fff;font:inherit;font-weight:600}
button:disabled{opacity:.5}
.empty{margin:auto;text-align:center;color:var(--muted);font-size:13px;line-height:1.8}
.error{margin:20px;padding:16px;border:1px solid #7a2d4a;background:#2a1220;border-radius:14px;color:#ffb9cf}
</style>
</head>
<body>
<header><div class="logo">星</div><div><div class="title">星坞 · 手机消息</div><div class="sub" id="sub">正在连接电脑…</div></div><div class="dot" id="dot"></div></header>
<div id="chat"><div class="empty" id="empty">与电脑上的星坞配对成功<br>在这里发送消息，电脑端会立即收到</div></div>
<footer><textarea id="text" placeholder="发送到电脑…" rows="1"></textarea><button id="send">发送</button></footer>
<script>
const token = new URLSearchParams(location.search).get('token') || '';
const chat = document.getElementById('chat');
const empty = document.getElementById('empty');
const input = document.getElementById('text');
const button = document.getElementById('send');
const sub = document.getElementById('sub');
const dot = document.getElementById('dot');
let last = 0, seen = new Set(), alive = false;

if (!token) {
  document.body.innerHTML = '<div class="error">配对链接缺少 token。请在电脑上重新生成二维码后再扫码。</div>';
} else {
  poll();
  setInterval(poll, 2000);
  button.onclick = send;
  input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } });
  input.addEventListener('input', () => { input.style.height = '44px'; input.style.height = Math.min(input.scrollHeight, 120) + 'px'; });
}

async function api(path, options) {
  const url = path + (path.includes('?') ? '&' : '?') + 'token=' + encodeURIComponent(token);
  const response = await fetch(url, options);
  if (!response.ok) throw new Error('HTTP ' + response.status);
  return response.json();
}

async function poll() {
  try {
    const data = await api('/api/messages?after=' + last);
    alive = true;
    dot.classList.add('on');
    sub.textContent = '电脑在线 · 消息留在本机';
    (data.messages || []).forEach(render);
  } catch (e) {
    alive = false;
    dot.classList.remove('on');
    sub.textContent = '未连接，正在重试…';
  }
}

function render(message) {
  if (seen.has(message.sequence)) return;
  seen.add(message.sequence);
  last = Math.max(last, message.sequence);
  empty?.remove();
  const row = document.createElement('div');
  row.className = 'msg ' + (message.direction === 'in' ? 'in' : 'out');
  const body = document.createElement('div');
  body.textContent = message.text;
  const meta = document.createElement('div');
  meta.className = 'meta';
  meta.textContent = message.sender + ' · ' + new Date(message.at).toLocaleTimeString('zh-CN', {hour:'2-digit',minute:'2-digit'});
  row.appendChild(body); row.appendChild(meta); chat.appendChild(row);
  chat.scrollTop = chat.scrollHeight;
}

async function send() {
  const text = input.value.trim();
  if (!text) return;
  button.disabled = true;
  try {
    await api('/api/messages', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text, sender: '手机', channel: 'phone' })
    });
    input.value = '';
    input.style.height = '44px';
    input.focus();
  } catch (e) {
    sub.textContent = '发送失败：' + e.message;
  } finally {
    button.disabled = false;
  }
}
</script>
</body>
</html>
""";
}
