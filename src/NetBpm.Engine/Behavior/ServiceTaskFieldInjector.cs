using System.Globalization;
using System.Reflection;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 服务任务字段注入器（<c>activiti:field</c>，任务清单 B2.1）。
/// 对应 Activiti 的 <c>ServiceTaskFields</c> / <c>FieldInjection</c>。
/// <para>
/// 注入语义（本子集，已登记偏差见下）：
/// <list type="bullet">
///   <item><description>字段名匹配委托实例上的<b>可写公共属性或公共字段</b>（区分大小写，同 Activiti）；</description></item>
///   <item><description><c>stringValue</c> 注入字面量；<c>expression</c> 在<b>每次执行时</b>对当前执行求值后注入；</description></item>
///   <item><description>目标成员不存在 / 值类型不可赋 → 响亮失败（不静默跳过）；</description></item>
///   <item><description>目标为 string 时任何值都可注入（ToString）；数值 / bool / DateTime 走 Convert。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>与 Activiti 相同的注意点</b>：委托实例通常是 DI 容器里的单例，
/// 注入会改写共享实例的成员 —— <c>expression</c> 字段在并发执行下互相覆盖。
/// 字段注入只应用于「部署期常量」（<c>stringValue</c> 或不依赖流程变量的表达式）；
/// 依赖执行上下文的取值请改为在委托内部读流程变量。
/// </para>
/// <para>
/// ⚠️ 与 Activiti 的差异：Activiti 对 <c>delegateExpression</c> 解析出的<b>原型 bean</b>
/// 在解析时注入一次；本子集统一在<b>每次执行时</b>注入（单例 / 原型行为一致）。
/// </para>
/// </summary>
internal static class ServiceTaskFieldInjector
{
    /// <summary>把服务任务的字段定义注入到委托实例上。</summary>
    public static async ValueTask InjectAsync(
        object target,
        IList<FieldExtension> fields,
        IDelegateExecution execution,
        NetBpm.El.IExpressionManager expressionManager,
        string serviceTaskId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(execution);

        foreach (var field in fields)
        {
            var name = field.FieldName
                ?? throw new EngineException($"服务任务 '{serviceTaskId}' 的 activiti:field 缺少 name 属性。");

            var member = FindMember(target.GetType(), name)
                ?? throw new EngineException(
                    $"服务任务 '{serviceTaskId}' 的字段 '{name}' 无法注入：委托 " +
                    $"{target.GetType().Name} 上没有同名的可写公共属性或公共字段。");

            var value = await ResolveFieldValueAsync(field, execution, expressionManager, serviceTaskId, name, ct)
                .ConfigureAwait(false);
            Assign(member, target, value, name, serviceTaskId);
        }
    }

    private static async ValueTask<object?> ResolveFieldValueAsync(
        FieldExtension field,
        IDelegateExecution execution,
        NetBpm.El.IExpressionManager expressionManager,
        string serviceTaskId,
        string fieldName,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(field.StringValue))
        {
            return field.StringValue;
        }

        if (!string.IsNullOrEmpty(field.Expression))
        {
            // 表达式求值失败直接上抛（部署期无法验证表达式，运行期响亮失败）
            return await expressionManager
                .CreateExpression(field.Expression)
                .GetValueAsync(execution, ct)
                .ConfigureAwait(false);
        }

        throw new EngineException(
            $"服务任务 '{serviceTaskId}' 的字段 '{fieldName}' 缺少取值（activiti:stringValue / activiti:expression 之一）。");
    }

    /// <summary>查找可写成员：属性优先（必须有 setter），其次公共字段。</summary>
    private static MemberInfo? FindMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        var property = type.GetProperty(name, flags);

        if (property is { CanWrite: true })
        {
            return property;
        }

        var field = type.GetField(name, flags);

        return field is { IsInitOnly: false } ? field : null;
    }

    private static void Assign(
        MemberInfo member,
        object target,
        object? value,
        string fieldName,
        string serviceTaskId)
    {
        var memberType = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo fieldInfo => fieldInfo.FieldType,
            _ => throw new EngineException($"Programmatic error: 不支持的成员类型 {member.GetType().Name}。"),
        };

        object? converted;

        if (memberType == typeof(string))
        {
            converted = value?.ToString();
        }
        else if (value is null || memberType.IsInstanceOfType(value))
        {
            converted = value;
        }
        else if (value is IConvertible && memberType.IsPrimitive || memberType == typeof(decimal))
        {
            try
            {
                converted = Convert.ChangeType(value, memberType, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                throw new EngineException(
                    $"服务任务 '{serviceTaskId}' 的字段 '{fieldName}' 无法把 " +
                    $"'{value}' 转换为 {memberType.Name}。", ex);
            }
        }
        else
        {
            throw new EngineException(
                $"服务任务 '{serviceTaskId}' 的字段 '{fieldName}' 的值类型 " +
                $"{value.GetType().Name} 与目标成员类型 {memberType.Name} 不兼容。");
        }

        switch (member)
        {
            case PropertyInfo property:
                property.SetValue(target, converted);
                break;

            case FieldInfo fieldInfo:
                fieldInfo.SetValue(target, converted);
                break;
        }
    }
}
