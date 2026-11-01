package cn.netbpm.platform.model;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;

/**
 * {@code StartInstanceResult}。
 *
 * <p>本文件由 {@code tools/NetBpm.SdkGen} 从 {@code contracts/openapi/workflow-v1.json}（OpenAPI 3.1.1）
 * 生成，请勿手改。重新生成：{@code dotnet run --project tools/NetBpm.SdkGen}</p>
 */
// 未知字段直接忽略：/workflow/v1 只增字段，忽略未知字段是契约对客户端的要求。
@JsonIgnoreProperties(ignoreUnknown = true)
public record StartInstanceResult(
        String businessKey,
        InstanceView instance,
        String instanceId,
        String processKey
) {
}
