namespace NetBpm.Bpmn.Model;

/// <summary>
/// 所有 BPMN 元素的基类。对应 Activiti 的 <c>BaseElement</c>。
/// 属性名与 Activiti 的 Java 属性名保持一致（开发计划 §1.1 L1/L2）。
/// </summary>
public abstract class BaseElement
{
    /// <summary>元素 id（对应 XML 属性 <c>id</c>）。</summary>
    public string? Id { get; set; }

    /// <summary>元素名称（对应 XML 属性 <c>name</c>）。</summary>
    public string? Name { get; set; }

    /// <summary>扩展元素（对应 <c>extensionElements</c>）。</summary>
    public IList<ExtensionElement> ExtensionElements { get; } = [];

    /// <summary>执行监听器（对应 <c>activiti:executionListener</c>，可选能力）。</summary>
    public IList<ExecutionListener> ExecutionListeners { get; } = [];

    /// <summary>任意命名空间下的扩展属性。</summary>
    public IDictionary<string, IList<ExtensionAttribute>> Attributes { get; } =
        new Dictionary<string, IList<ExtensionAttribute>>(StringComparer.Ordinal);

    /// <summary>按命名空间取扩展属性。</summary>
    public IList<ExtensionAttribute> GetAttribute(string namespaceUri, string name)
    {
        var key = $"{namespaceUri}:{name}";
        return Attributes.TryGetValue(key, out var value) ? value : [];
    }

    public void AddAttribute(string namespaceUri, string name, string value)
    {
        var key = $"{namespaceUri}:{name}";
        if (!Attributes.TryGetValue(key, out var list))
        {
            list = [];
            Attributes[key] = list;
        }

        list.Add(new ExtensionAttribute(namespaceUri, name, value));
    }
}

/// <summary>自定义扩展元素（对应 <c>extensionElements</c> 下的任意子元素）。</summary>
public class ExtensionElement : BaseElement
{
    /// <summary>元素本地名（如 <c>field</c>）。</summary>
    public string? ElementName { get; set; }

    /// <summary>元素所在命名空间（如 <c>http://activiti.org/bpmn</c>）。</summary>
    public string? NamespacePrefix { get; set; }

    /// <summary>元素的文本内容。</summary>
    public string? ElementText { get; set; }
}

/// <summary>扩展属性（命名空间 + 名称 + 值）。</summary>
#pragma warning disable CA1711 // 类名与 Activiti 的 ExtensionAttribute 对齐（§1.1 L2）
public sealed record ExtensionAttribute(string NamespaceUri, string Name, string Value);
#pragma warning restore CA1711

/// <summary>执行监听器（<c>activiti:executionListener</c>）。</summary>
public class ExecutionListener : BaseElement
{
    public const string EventStart = "start";
    public const string EventEnd = "end";

    /// <summary>触发时机：<c>start</c> / <c>end</c>。</summary>
    public string? Event { get; set; }

    /// <summary>实现类全名（<c>class</c>）。</summary>
    public string? ClassName { get; set; }

    /// <summary>表达式（<c>expression</c>）。</summary>
    public string? Expression { get; set; }

    /// <summary>委托表达式（<c>delegateExpression</c>）。</summary>
    public string? DelegateExpression { get; set; }

    /// <summary>自定义实现类型（<c>activiti:type</c>）。</summary>
    public string? ImplementationType { get; set; }

    /// <summary>字段注入（<c>activiti:field</c>）。</summary>
    public IList<FieldExtension> Fields { get; } = [];
}

/// <summary>任务监听器（<c>activiti:taskListener</c>）。</summary>
public class TaskListener : ExecutionListener
{
    public const string EventCreate = "create";
    public const string EventAssignment = "assignment";
    public const string EventComplete = "complete";
    public const string EventDelete = "delete";
}

/// <summary>字段注入（<c>activiti:field</c>）。</summary>
public class FieldExtension : BaseElement
{
    /// <summary>字段名。</summary>
    public string? FieldName { get; set; }

    /// <summary>字符串值（<c>stringValue</c>）。</summary>
    public string? StringValue { get; set; }

    /// <summary>表达式值（<c>expression</c>）。</summary>
    public string? Expression { get; set; }
}

/// <summary>潜在启动者（<c>activiti:potentialStarter</c>）。</summary>
public class PotentialStarter : BaseElement
{
    /// <summary>资源引用文本（候选人 / 候选组）。</summary>
    public string? ResourceAssignmentExpression { get; set; }
}
