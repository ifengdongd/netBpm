using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NetBpm.SdkGen;

/// <summary>
/// Java 产物：<c>sdk/java</c> 下的一组 record / enum 模型 + Maven 工程骨架。
/// </summary>
/// <remarks>
/// <para>
/// 为什么 Java 侧只生成模型、不生成客户端：官方 SDK 是 .NET 那一套（<c>src/NetBpm.Platform.Client</c>），
/// 再手写第二个 HTTP 客户端只会得到两份「谁先支持新端点」的分叉。Java 侧的真实痛点是
/// <b>DTO 靠人工对着文档抄</b>——抄错一个字段名，序列化就静默丢字段。所以这里只解决那件事：
/// 模型从快照生成，客户端用 <c>java.net.http</c> + Jackson 三五行即可（见 <c>sdk/README.md</c>）。
/// </para>
/// <para>
/// 时间字段刻意映射成 <c>String</c>：平台一律下发 ISO-8601 文本，映射成 <c>OffsetDateTime</c>
/// 会强制使用方注册 <c>jackson-datatype-jsr310</c>，并让「服务端多给几位小数」变成反序列化失败。
/// 保真优先于便利。
/// </para>
/// </remarks>
public sealed class JavaEmitter(ContractDocument contract)
{
    private const string Package = "cn.netbpm.platform.model";
    private const string PackagePath = "sdk/java/src/main/java/cn/netbpm/platform/model";

    /// <summary>Java 关键字 + 字面量：出现在字段名上必须改名并保留 JSON 名。</summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if",
        "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "package", "private",
        "protected", "public", "record", "return", "sealed", "short", "static", "strictfp", "super", "switch",
        "synchronized", "this", "throw", "throws", "transient", "try", "var", "void", "volatile", "while", "yield",
        "true", "false", "null",
        // 记录组件名与方法名冲突（record 自带 accessor，class 里不能再有同名成员）
        "wait", "notify", "notifyAll", "hashCode", "equals", "toString", "getClass", "clone", "finalize",
    };

    public IEnumerable<GeneratedFile> Emit()
    {
        yield return new GeneratedFile("sdk/java/pom.xml", Pom());

        foreach (var name in contract.ReachableSchemaNames)
        {
            if (contract.Schema(name) is not JsonObject map)
            {
                continue;
            }

            var values = Schemas.EnumValues(map);

            var content = values.Count > 0 && Schemas.Types(map).Count == 0
                ? Enum(name, values)
                : Record(name, map);

            yield return new GeneratedFile($"{PackagePath}/{name}.java", content);
        }
    }

    private string Enum(string name, IReadOnlyList<string> values)
    {
        var builder = new StringBuilder();

        builder.Append(Header());
        builder.AppendLine();
        builder.AppendLine("import com.fasterxml.jackson.annotation.JsonEnumDefaultValue;");
        builder.AppendLine();
        builder.Append(Summary($"{{@code {name}}}（枚举）。"));
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"public enum {name} {{");

        foreach (var value in values)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"    {Identifier(value)},");
        }

        builder.AppendLine();
        builder.AppendLine("    // 兜底：服务端新增取值时落到这里，而不是让反序列化抛异常。");
        builder.AppendLine("    // 需要开启 DeserializationFeature.READ_UNKNOWN_ENUM_VALUES_USING_DEFAULT_VALUE。");
        builder.AppendLine("    @JsonEnumDefaultValue");
        builder.AppendLine("    UNKNOWN");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private string Record(string name, JsonObject map)
    {
        var properties = map["properties"] as JsonObject;

        var components = new List<(string Name, string Type, string? JsonName)>();
        var usesList = false;
        var usesMap = false;
        var usesJsonProperty = false;

        foreach (var (property, schema) in properties ?? [])
        {
            var type = TypeMapper.Java(schema);

            usesList |= type.Contains("List<", StringComparison.Ordinal);
            usesMap |= type.Contains("Map<", StringComparison.Ordinal);

            var component = Identifier(property);
            var renamed = !string.Equals(component, property, StringComparison.Ordinal);

            usesJsonProperty |= renamed;

            components.Add((component, type, renamed ? property : null));
        }

        var builder = new StringBuilder();

        builder.Append(Header());
        builder.AppendLine();
        builder.AppendLine("import com.fasterxml.jackson.annotation.JsonIgnoreProperties;");

        if (usesJsonProperty)
        {
            builder.AppendLine("import com.fasterxml.jackson.annotation.JsonProperty;");
        }

        if (usesList || usesMap)
        {
            builder.AppendLine();

            if (usesList)
            {
                builder.AppendLine("import java.util.List;");
            }

            if (usesMap)
            {
                builder.AppendLine("import java.util.Map;");
            }
        }

        builder.AppendLine();
        builder.Append(Summary($"{{@code {name}}}。"));
        builder.AppendLine();

        // §7.2：客户端必须容忍未知字段——服务端只增字段，不打招呼
        builder.AppendLine("// 未知字段直接忽略：/workflow/v1 只增字段，忽略未知字段是契约对客户端的要求。");
        builder.AppendLine("@JsonIgnoreProperties(ignoreUnknown = true)");

        if (components.Count == 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"public record {name}() {{");
            builder.AppendLine("}");

            return builder.ToString();
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"public record {name}(");

        for (var index = 0; index < components.Count; index++)
        {
            var (component, type, jsonName) = components[index];
            var separator = index == components.Count - 1 ? string.Empty : ",";
            var annotation = jsonName is null ? string.Empty : $"@JsonProperty({TypeMapper.JavaStringLiteral(jsonName)}) ";

            builder.AppendLine(CultureInfo.InvariantCulture, $"        {annotation}{type} {component}{separator}");
        }

        builder.AppendLine(") {");
        builder.AppendLine("}");

        return builder.ToString();
    }

    /// <summary>文件头：只写 <c>package</c>；import 在前、类型说明贴在类型上（Java 的注释位置有语义）。</summary>
    private static string Header() => """
    package cn.netbpm.platform.model;

    """;

    /// <summary>类型说明（Javadoc），贴在类型声明正上方。</summary>
    private string Summary(string summary) => $$"""
    /**
     * {{summary}}
     *
     * <p>本文件由 {@code tools/NetBpm.SdkGen} 从 {@code contracts/openapi/workflow-v1.json}（OpenAPI {{contract.OpenApiVersion}}）
     * 生成，请勿手改。重新生成：{@code dotnet run --project tools/NetBpm.SdkGen}</p>
     */
    """;

    private static string Pom() => """
    <?xml version="1.0" encoding="UTF-8"?>
    <!--
      由 tools/NetBpm.SdkGen 生成，请勿手改。
      产物只有模型（record / enum），没有客户端：官方客户端是 .NET 的 NetBpm.Platform.Client。
      校验：mvn -q -o compile（-o 走本地仓库，避免 CI 之外的网络依赖）
    -->
    <project xmlns="http://maven.apache.org/POM/4.0.0"
             xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
             xsi:schemaLocation="http://maven.apache.org/POM/4.0.0 http://maven.apache.org/xsd/maven-4.0.0.xsd">
      <modelVersion>4.0.0</modelVersion>

      <groupId>cn.netbpm</groupId>
      <artifactId>netbpm-platform-sdk</artifactId>
      <version>1.0.0</version>
      <packaging>jar</packaging>

      <properties>
        <maven.compiler.release>21</maven.compiler.release>
        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
        <jackson.version>2.17.2</jackson.version>
      </properties>

      <dependencies>
        <!-- 只依赖注解包：模型只用 @JsonIgnoreProperties / @JsonProperty / @JsonEnumDefaultValue，
             反序列化用哪个 jackson-databind 版本由使用方决定（避免把它的升级节奏绑进 SDK） -->
        <dependency>
          <groupId>com.fasterxml.jackson.core</groupId>
          <artifactId>jackson-annotations</artifactId>
          <version>${jackson.version}</version>
        </dependency>
      </dependencies>
    </project>

    """;

    /// <summary>
    /// 字段名 → Java 标识符：关键字与「会与 record 自带方法撞名」的写法统一加下划线，
    /// 真实名由 <c>@JsonProperty</c> 保住（否则序列化会静默丢字段）。
    /// </summary>
    private static string Identifier(string name)
    {
        var candidate = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_')
            ? name
            : "_" + name;

        candidate = new string([.. candidate.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')]);

        return ReservedWords.Contains(candidate) ? candidate + "_" : candidate;
    }
}
