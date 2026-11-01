using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class PhoneBridgeAndWeChatTests
{
    [Fact]
    public void NewToken_is_16_hex_chars_and_unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => PhoneBridgeService.NewToken()).ToList();

        Assert.All(tokens, token => Assert.Matches("^[0-9a-f]{16}$", token));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void AppendInbound_assigns_increasing_sequence_and_raises_event()
    {
        using var bridge = new PhoneBridgeService();
        var seen = new List<BridgeEvent>();
        bridge.MessageAppended += (_, message) => seen.Add(new BridgeEvent(message.Sequence, message.Channel));

        bridge.AppendInbound("第一条", "手机");
        bridge.AppendInbound("第二条", "微信", "wechat");

        Assert.Equal(2, seen.Count);
        Assert.Equal(1L, seen[0].Sequence);
        Assert.Equal(2L, seen[1].Sequence);
        Assert.Equal("phone", seen[0].Channel);
        Assert.Equal("wechat", seen[1].Channel);

        var increment = bridge.MessagesSince(1);
        Assert.Single(increment);
        Assert.Equal(2L, increment[0].Sequence);
    }

    [Fact]
    public void Outbound_messages_are_kept_and_trimmed_to_capacity()
    {
        using var bridge = new PhoneBridgeService();
        for (var index = 0; index < PhoneBridgeService.MaxMessages + 20; index++)
        {
            bridge.AppendOutbound($"m{index}");
        }

        var snapshot = bridge.Snapshot();
        Assert.Equal(PhoneBridgeService.MaxMessages, snapshot.Count);
        Assert.Equal(PhoneBridgeService.MaxMessages + 20, snapshot[^1].Sequence);
        Assert.False(snapshot[^1].IsInbound);
    }

    [Fact]
    public void Append_rejects_empty_text_and_truncates_long_text()
    {
        using var bridge = new PhoneBridgeService();
        Assert.Throws<ArgumentException>(() => bridge.AppendInbound("   "));

        var longText = new string('a', PhoneBridgeService.MaxTextLength + 50);
        var message = bridge.AppendOutbound(longText);

        Assert.Equal(PhoneBridgeService.MaxTextLength, message.Text.Length);
    }

    [Fact]
    public void ClearMessages_keeps_sequence_monotonic()
    {
        using var bridge = new PhoneBridgeService();
        bridge.AppendOutbound("第一条");
        bridge.ClearMessages();

        Assert.Empty(bridge.Snapshot());
        Assert.Equal(2L, bridge.AppendInbound("第二条").Sequence);
    }

    [Fact]
    public void Configure_normalizes_invalid_port_and_generates_token()
    {
        using var bridge = new PhoneBridgeService();
        bridge.Configure(false, 80, "");

        Assert.Equal(PhoneBridgeService.DefaultPort, bridge.Port);
        Assert.Equal(16, bridge.Token.Length);
        Assert.False(bridge.IsRunning);
        Assert.False(bridge.Enabled);
    }

    [Fact]
    public async Task Http_bridge_rejects_missing_token_and_reads_incremental_messages()
    {
        using var bridge = new PhoneBridgeService();
        var port = FindFreePort();
        bridge.Configure(true, port, "test-token-1234");

        // 端口被占用或平台限制时给明确失败信息，避免误判为逻辑错误。
        Assert.True(bridge.IsRunning, $"手机桥未能启动：{bridge.LastError}");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var unauthorized = await client.GetAsync($"http://127.0.0.1:{port}/api/messages");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var post = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/inbound?token=test-token-1234",
            new StringContent(
                """{"text":"来自微信","sender":"微信","channel":"wechat"}""",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var body = await client.GetStringAsync(
            $"http://127.0.0.1:{port}/api/messages?after=0&token=test-token-1234");
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());

        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("来自微信", messages[0].GetProperty("text").GetString());
        Assert.Equal("wechat", messages[0].GetProperty("channel").GetString());
    }

    [Fact]
    public void OpenClaw_config_points_default_model_at_local_endpoint()
    {
        var json = WeChatRelayService.BuildOpenClawConfig("http://127.0.0.1:8090/v1/", "", "Qwen3-8B");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var provider = root.GetProperty("models").GetProperty("providers").GetProperty("localmodelhub");
        Assert.Equal("http://127.0.0.1:8090/v1", provider.GetProperty("baseUrl").GetString());
        Assert.Equal("local", provider.GetProperty("apiKey").GetString());
        Assert.Equal("openai-completions", provider.GetProperty("api").GetString());
        Assert.Equal("Qwen3-8B", provider.GetProperty("models")[0].GetProperty("id").GetString());

        Assert.Equal(
            "localmodelhub/Qwen3-8B",
            root.GetProperty("agents").GetProperty("defaults").GetProperty("model").GetProperty("primary").GetString());
        Assert.Equal("merge", root.GetProperty("models").GetProperty("mode").GetString());
    }

    [Fact]
    public void OpenClaw_arguments_validate_channel_and_target()
    {
        var args = WeChatRelayService.BuildOpenClawArguments("openclaw-weixin", " wxid_abc ", " 你好 ");

        Assert.Equal(
            new[] { "message", "send", "--channel", "openclaw-weixin", "--target", "wxid_abc", "--message", "你好" },
            args);
        Assert.Throws<InvalidOperationException>(
            () => WeChatRelayService.BuildOpenClawArguments("OpenClaw/WeiXin", "wxid_abc", "hi"));
        Assert.Throws<InvalidOperationException>(
            () => WeChatRelayService.BuildOpenClawArguments("openclaw-weixin", "   ", "hi"));
    }

    [Fact]
    public void WeCom_payload_is_trimmed_text_message()
    {
        using var document = JsonDocument.Parse(WeChatRelayService.BuildWeComPayload("  测试消息  "));

        Assert.Equal("text", document.RootElement.GetProperty("msgtype").GetString());
        Assert.Equal("测试消息", document.RootElement.GetProperty("text").GetProperty("content").GetString());
    }

    [Fact]
    public async Task SendAsync_without_mode_is_rejected()
    {
        using var relay = new WeChatRelayService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SendAsync(
            new WeChatRelayOptions("none", "openclaw", "openclaw-weixin", "", ""),
            "你好",
            CancellationToken.None));
    }

    [Fact]
    public async Task Webhook_mode_rejects_invalid_url()
    {
        using var relay = new WeChatRelayService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SendAsync(
            new WeChatRelayOptions("webhook", "openclaw", "openclaw-weixin", "", "not-a-url"),
            "你好",
            CancellationToken.None));
    }

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private readonly record struct BridgeEvent(long Sequence, string Channel);
}
