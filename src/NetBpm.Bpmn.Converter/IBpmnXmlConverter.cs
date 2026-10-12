using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Converter;

/// <summary>
/// BPMN XML ⇄ 模型的转换入口。对应 Activiti 的 <c>BpmnXMLConverter</c>。
/// <para>
/// ⚠️ 必须保持<b>流式</b>读写（<c>XmlReader</c> / <c>XmlWriter</c>），不整树加载 ——
/// 大流程文件的关键（开发计划 §3）。
/// </para>
/// </summary>
public interface IBpmnXmlConverter
{
    /// <summary>XML → 模型。</summary>
    /// <exception cref="BpmnXmlParseException">XML 格式或元素不受支持。</exception>
    BpmnModel ConvertToBpmnModel(Stream xmlStream);

    /// <summary>模型 → XML（往返保真：DI 坐标与扩展属性零丢失，见 P2 验收标准）。</summary>
    void ConvertToXml(BpmnModel bpmnModel, Stream outputStream);
}

/// <summary>BPMN XML 解析失败的异常。</summary>
public sealed class BpmnXmlParseException : Exception
{
    public BpmnXmlParseException(string message)
        : base(message)
    {
    }

    public BpmnXmlParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
