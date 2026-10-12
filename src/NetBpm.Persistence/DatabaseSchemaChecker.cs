namespace NetBpm.Persistence;

/// <summary>
/// 数据库结构版本校验。对应 Activiti 的 <c>SchemaManager</c> 中的 <c>schema.version</c> 检查
/// （P5 验收标准之一）。
/// </summary>
public sealed class DatabaseSchemaChecker
{
    /// <summary>本方案对齐的 Activiti 结构版本（见 §7.3 的 DDL 初值）。</summary>
    public const string ExpectedSchemaVersion = "8.1.4";

    private readonly IPropertyStore _propertyStore;

    public DatabaseSchemaChecker(IPropertyStore propertyStore)
    {
        ArgumentNullException.ThrowIfNull(propertyStore);
        _propertyStore = propertyStore;
    }

    /// <summary>校验 <c>ACT_GE_PROPERTY</c> 已初始化且版本匹配。</summary>
    /// <exception cref="Engine.EngineException">未初始化或版本不匹配。</exception>
    public void Check()
    {
        var schemaVersion = _propertyStore.FindProperty("schema.version");

        if (schemaVersion is null)
        {
            throw new Engine.EngineException(
                "数据库尚未初始化：ACT_GE_PROPERTY 中缺少 'schema.version'。" +
                "请先执行 db/sqlserver 或 db/mysql 下的建表与初始化脚本。");
        }

        if (!string.Equals(schemaVersion.Value, ExpectedSchemaVersion, StringComparison.Ordinal))
        {
            throw new Engine.EngineException(
                $"数据库结构版本不匹配：库中为 '{schemaVersion.Value}'，本引擎期望 '{ExpectedSchemaVersion}'。");
        }

        var nextDbId = _propertyStore.FindProperty(DbIdGenerator.NextDbIdPropertyName);
        if (nextDbId is null)
        {
            throw new Engine.EngineException(
                $"数据库尚未初始化：ACT_GE_PROPERTY 中缺少 '{DbIdGenerator.NextDbIdPropertyName}'。");
        }
    }
}
