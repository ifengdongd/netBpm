using NetBpm.Rest.Hosting;

var builder = WebApplication.CreateBuilder(args);

// 组合根（与管理端宿主共享）：引擎 / 持久化 / 身份 / 作业 / outbox / 审计 / 异常 / 限流 / 探针。
// 限流阈值走 NetBpm:RateLimit:*，请求体上限走 NetBpm:MaxRequestBodySizeBytes（默认 10 MB）。
var hostOptions = builder.AddNetBpmHost();

var app = builder.Build();

// 管道：异常处理 → 限流 → 鉴权 → 幂等（未配置 NetBpm:Security:ApiKey 时鉴权放行）
app.UseNetBpmHost(hostOptions);

// 探针 / 运维信息 / 设计器 / 运行态 / 身份 / 登录端点 + OpenAPI
app.MapNetBpmHostEndpoints(hostOptions);

// 数据库模式：执行幂等初始化脚本（库里缺什么补什么），再校验 ACT_GE_PROPERTY.schema.version
app.ApplyNetBpmDatabaseStartup();

app.Run();

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;
