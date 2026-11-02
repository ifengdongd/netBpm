namespace NetBpm.Engine.Command;

/// <summary>
/// 命令级 DI 作用域：服务任务委托 / 监听器在<b>本次命令</b>的作用域内解析，命令结束（无论成败）时释放。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：解析器此前拿到的是<b>根</b> <see cref="IServiceProvider"/>。用它解析
/// <c>AddScoped</c> 注册的委托只有两种结果 —— 要么容器开了作用域校验（<c>ValidateScopes</c>）时直接抛
/// <c>InvalidOperationException</c>，要么更糟：拿到一份「根作用域」实例被所有请求共享，
/// 而它内部若持有 <c>DbContext</c> / 仓储，就是跨请求共享可变状态（典型的一类隐蔽串号 bug）。
/// </para>
/// <para>
/// 引擎不认识 DI 容器（Engine 层不引用 <c>Microsoft.Extensions.DependencyInjection</c>），
/// 所以这里只约定「一个服务提供者 + 可释放」两件事，具体作用域由组合根（Runtime）创建。
/// </para>
/// <para>
/// 🔑 生命周期必须**恰好**是命令：委托实例不跨命令复用（解析发生在每次执行时），
/// 所以命令结束即释放既不会悬空引用，也不会把已释放的服务留给下一条命令。
/// </para>
/// </remarks>
public interface ICommandServiceScope : IDisposable
{
    /// <summary>作用域内的服务提供者。</summary>
    IServiceProvider Services { get; }
}
