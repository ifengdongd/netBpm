using NetBpm.Persistence;
using NetBpm.Rest.Endpoints;
using NetBpm.Runtime;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI（P9 验收标准：Swagger 全端点可用）
builder.Services.AddOpenApi();

// 组合根：模型 / 转换 / 校验 / 表达式 / 引擎 / 持久化 / 身份
builder.Services.AddNetBpm(builder.Configuration);

var app = builder.Build();

app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = "NetBpm",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
}));

app.MapDesignerEndpoints();
app.MapRuntimeEndpoints();

// 数据库模式：先按需建库/建表，再校验 ACT_GE_PROPERTY.schema.version（P5 验收标准之一）
var netBpmOptions = app.Services.GetRequiredService<NetBpmOptions>();

if (netBpmOptions.UsesDatabase)
{
    if (netBpmOptions.AutoMigrate)
    {
        var migration = app.Services.GetRequiredService<DatabaseMigrator>().Migrate();

        if (migration.AlreadyInitialized)
        {
            app.Logger.LogInformation("数据库已初始化，跳过建表脚本。");
        }
        else
        {
            app.Logger.LogInformation("已执行建表脚本：{Scripts}", string.Join(", ", migration.AppliedScripts));
        }
    }

    if (netBpmOptions.ValidateSchemaOnStartup)
    {
        app.Services.GetRequiredService<DatabaseSchemaChecker>().Check();
        app.Logger.LogInformation("数据库结构版本校验通过：schema.version = {Version}", DatabaseSchemaChecker.ExpectedSchemaVersion);
    }
}

app.Run();

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;
