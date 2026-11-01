using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class IdleSleepServiceTests
{
    [Fact]
    public async Task Sleeps_after_timeout()
    {
        using var service = new IdleSleepService(TimeSpan.FromMilliseconds(80));
        var slept = new TaskCompletionSource();

        service.Slept += () => slept.TrySetResult();
        service.Start();

        await slept.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(service.IsSleeping);
    }

    [Fact]
    public async Task Stop_prevents_sleeping()
    {
        using var service = new IdleSleepService(TimeSpan.FromMilliseconds(80));
        var slept = false;

        service.Slept += () => slept = true;
        service.Start();
        service.Stop();

        await Task.Delay(250);
        Assert.False(slept);
        Assert.False(service.IsSleeping);
    }

    [Fact]
    public async Task Before_sleep_runs_before_the_sleep_event()
    {
        using var service = new IdleSleepService(TimeSpan.FromMilliseconds(80));
        var order = new List<string>();
        var slept = new TaskCompletionSource();

        service.BeforeSleep += () => order.Add("before");
        service.Slept += () =>
        {
            order.Add("slept");
            slept.TrySetResult();
        };
        service.Start();

        await slept.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["before", "slept"], order);
    }

    [Fact]
    public async Task Failing_before_sleep_handler_does_not_block_sleep()
    {
        using var service = new IdleSleepService(TimeSpan.FromMilliseconds(80));
        var slept = new TaskCompletionSource();

        service.BeforeSleep += () => throw new InvalidOperationException("整理失败");
        service.Slept += () => slept.TrySetResult();
        service.Start();

        await slept.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(service.IsSleeping);
    }

    [Fact]
    public async Task Disabled_service_never_sleeps()
    {
        using var service = new IdleSleepService(TimeSpan.FromMilliseconds(50));
        var slept = false;

        service.Slept += () => slept = true;
        service.Configure(0);
        service.Start();

        await Task.Delay(200);
        Assert.False(slept);
        Assert.False(service.Enabled);
    }
}
