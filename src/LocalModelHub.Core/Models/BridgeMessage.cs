namespace LocalModelHub.Core.Models;

/// <summary>
/// 手机 / 微信桥中的一条消息。Direction 为 in（手机 → 电脑）或 out（电脑 → 手机）。
/// Sequence 由桥服务单调递增，移动端用它做增量拉取。
/// </summary>
public sealed record BridgeMessage(
    long Sequence,
    string Direction,
    string Text,
    string Sender,
    string Channel,
    DateTimeOffset At)
{
    public bool IsInbound => Direction == "in";

    public string AtText => At.ToLocalTime().ToString("HH:mm");
}
