using LocalModelHub.App.Services;
using Xunit;

namespace LocalModelHub.Ui.Tests;

/// <summary>P2-1：显式注册表解析已注册实例，缺失时抛出带类型名的异常。</summary>
public class ServiceRegistryTests
{
    private sealed class DummyService
    {
        public string Name => "dummy";
    }

    [Fact]
    public void Registry_resolves_registered_instance_and_reports_missing()
    {
        var registry = new ServiceRegistry();
        var dummy = new DummyService();
        registry.Register(dummy);

        Assert.True(registry.TryResolve<DummyService>(out var resolved));
        Assert.Same(dummy, resolved);

        var error = Assert.Throws<InvalidOperationException>(() => registry.Resolve<string>());
        Assert.Contains("String", error.Message);
        Assert.False(registry.TryResolve<string>(out _));
    }
}
