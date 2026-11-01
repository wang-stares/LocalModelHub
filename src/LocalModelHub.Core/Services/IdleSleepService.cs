namespace LocalModelHub.Core.Services;

/// <summary>空闲休眠计时器：Start/Poke 重新计时，超时后先触发 BeforeSleep（整理等收尾），再触发 Slept。</summary>
public sealed class IdleSleepService : IDisposable
{
    private readonly object _sync = new();
    private readonly TimeSpan _defaultTimeout;
    private TimeSpan _timeout;
    private Timer? _timer;
    private int _generation;

    public IdleSleepService(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(10);
        _timeout = _defaultTimeout;
    }

    public bool IsSleeping { get; private set; }

    public bool Enabled => _timeout > TimeSpan.Zero;

    public TimeSpan Timeout => _timeout;

    /// <summary>休眠前的收尾工作（整理记忆、保存状态）；失败不阻断休眠。</summary>
    public event Action? BeforeSleep;

    public event Action? Slept;

    /// <summary>按分钟配置休眠时间；0 表示关闭。</summary>
    public void Configure(int minutes)
    {
        lock (_sync)
        {
            _timeout = minutes <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(minutes);
            if (!Enabled) IsSleeping = false;
            RestartLocked();
        }
    }

    /// <summary>开始（或重新开始）倒计时。</summary>
    public void Start()
    {
        lock (_sync)
        {
            IsSleeping = false;
            RestartLocked();
        }
    }

    public void Poke() => Start();

    /// <summary>取消倒计时并清除休眠状态（手动停止服务或开始生成时调用）。</summary>
    public void Stop()
    {
        lock (_sync)
        {
            IsSleeping = false;
            CancelLocked();
        }
    }

    private void RestartLocked()
    {
        CancelLocked();
        if (!Enabled) return;

        var generation = ++_generation;
        _timer = new Timer(_ => OnTick(generation), null, _timeout, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void CancelLocked()
    {
        _timer?.Dispose();
        _timer = null;
        _generation++;
    }

    private void OnTick(int generation)
    {
        lock (_sync)
        {
            if (generation != _generation || !Enabled) return;

            IsSleeping = true;
            _timer?.Dispose();
            _timer = null;
        }

        var beforeSleep = BeforeSleep;
        if (beforeSleep is not null)
        {
            // 每个收尾处理器独立容错：整理失败也要进入休眠。
            foreach (var handler in beforeSleep.GetInvocationList().Cast<Action>())
            {
                try
                {
                    handler();
                }
                catch
                {
                    // 收尾失败不影响休眠
                }
            }
        }

        Slept?.Invoke();
    }

    public void Dispose()
    {
        lock (_sync) CancelLocked();
    }
}
