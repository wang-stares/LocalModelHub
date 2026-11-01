namespace LocalModelHub.App.Services;

/// <summary>
/// 显式服务注册表：不使用反射，按类型字典查找；缺失时抛出带类型名的异常，便于定位漏注册。
/// </summary>
public sealed class ServiceRegistry
{
    private readonly Dictionary<Type, object> _services = [];

    public void Register<T>(T instance) where T : notnull => _services[typeof(T)] = instance;

    public T Resolve<T>() where T : notnull =>
        TryResolve<T>(out var service)
            ? service
            : throw new InvalidOperationException($"未注册服务：{typeof(T).Name}");

    public bool TryResolve<T>(out T service) where T : notnull
    {
        if (_services.TryGetValue(typeof(T), out var instance) && instance is T typed)
        {
            service = typed;
            return true;
        }

        service = default!;
        return false;
    }
}
