using System.Text;
using NetBpm.Bpmn.Converter;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 部署原始字节（任务清单 B2.5）：调用方给的 XML 原文必须<b>原样</b>存进
/// <c>ACT_GE_BYTEARRAY.BYTES_</c>（与 Activiti 一致），而不是重新序列化的结果。
/// </summary>
[Collection("MySql")]
public sealed class MySqlDeploymentRawBytesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeployRawBytes_ShouldStoreOriginalXmlInByteArray()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        // 重新序列化会丢 XML 注释与属性顺序 —— 注释是「原文 vs 重序列化」最简单的判别物
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- 原始字节测试：这段注释只在 BYTES_ 存原文时能存活 -->
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         targetNamespace="http://netbpm/test">
              <process id="raw-bytes-db" isExecutable="true">
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        await harness.Definitions.DeployAsync(
            model,
            "test.bpmn20.xml",
            rawResourceBytes: Encoding.UTF8.GetBytes(xml),
            tenantId: null,
            ct: Ct);

        using var session = harness.SessionFactory.Create();

        var rows = RowValues.Query(
            session,
            "select NAME_, BYTES_ from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is not null");

        var row = Assert.Single(rows);

        Assert.Equal("test.bpmn20.xml", RowValues.Text(row, "NAME_"));
        Assert.Equal(xml, RowValues.TextFromBytes(row, "BYTES_"));

        // 语义不受影响：解析回读（GetBpmnModelAsync 走的是解析路径）
        Assert.NotNull(await harness.Definitions.GetBpmnModelAsync(
            (await harness.Definitions.FindAllAsync(ct: Ct)).Single().Id,
            Ct));
    }
}
