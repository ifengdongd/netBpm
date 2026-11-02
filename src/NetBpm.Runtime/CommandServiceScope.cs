using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Command;

namespace NetBpm.Runtime;

/// <summary>
/// <see cref="ICommandServiceScope"/> 的 DI 实现：一条命令一个 <see cref="IServiceScope"/>。
/// </summary>
/// <remarks>
/// <para>
/// 用 <see cref="IServiceScopeFactory"/> 从<b>根</b>容器开子作用域，而不是复用宿主的请求作用域：
/// 引擎的命令来源不只有 HTTP（定时器、异步作业、外部消息都在各自的后台循环里跑），
/// 那些路径没有请求作用域可用；统一在命令边界上开作用域，三类入口就都成立。
/// </para>
/// <para>
/// 代价是 HTTP 路径上多一层作用域 —— 引擎服务本身都是单例，委托类才可能命中 Scoped，
/// 未使用委托的命令连作用域都不会创建（<see cref="CommandContext.ScopedServices"/> 是惰性的）。
/// </para>
/// </remarks>
internal sealed class CommandServiceScope : ICommandServiceScope
{
    private readonly IServiceScope _scope;

    public CommandServiceScope(IServiceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope;
    }

    /// <inheritdoc />
    public IServiceProvider Services => _scope.ServiceProvider;

    /// <inheritdoc />
    public void Dispose() => _scope.Dispose();
}
