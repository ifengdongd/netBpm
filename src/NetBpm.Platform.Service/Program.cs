using NetBpm.Platform.Service;
using NetBpm.Rest.Hosting;

/*
 * Platform.Service —— NetBpm 的「独立部署形态」宿主。
 *
 * 与 NetBpm.Rest 的区别不在能力（两者共用同一套组合根），而在默认值：
 * 演示宿主默认开放、默认内存；本宿主默认要求凭据、默认连数据库，并在启动自检不过时直接失败。
 * 分开两个入口是为了让「忘记关 RequireCredential」这类错误不可能发生，而不是靠发版清单提醒自己。
 */

var builder = WebApplication.CreateBuilder(args);

PlatformServiceBootstrap.Configure(builder);

// 先于组合根：这样「缺环境变量」拿到的是可直接照抄的修复动作
PlatformServiceGuards.VerifyConfiguration(builder.Configuration);

// 组合根（与演示宿主共享）：引擎 / 持久化 / 身份 / 作业 / outbox / 事件投递 / 审计 / 异常 / 限流 / 探针
var hostOptions = builder.AddNetBpmHost();

var app = builder.Build();

// 反向代理头必须在鉴权之前处理（UseNetBpmHost 内部会挂中间件），否则回调与重定向会指向错误宿主
app.UseForwardedHeaders();

// 启动自检：配置缺失时宁可起不来，也不要「起来却是残的」（必须早于 AddNetBpmHost 的错误文案）
PlatformServiceGuards.WriteBanner(app);

app.UseNetBpmHost(hostOptions);

// 探针 / 运维信息 / 设计器 / 运行态 / 身份 / 登录端点 + OpenAPI
app.MapNetBpmHostEndpoints(hostOptions);

// 数据库模式：执行幂等初始化脚本后校验 schema.version
app.ApplyNetBpmDatabaseStartup();

app.Run();

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;
