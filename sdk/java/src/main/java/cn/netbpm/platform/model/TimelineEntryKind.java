package cn.netbpm.platform.model;

import com.fasterxml.jackson.annotation.JsonEnumDefaultValue;

/**
 * {@code TimelineEntryKind}（枚举）。
 *
 * <p>本文件由 {@code tools/NetBpm.SdkGen} 从 {@code contracts/openapi/workflow-v1.json}（OpenAPI 3.1.1）
 * 生成，请勿手改。重新生成：{@code dotnet run --project tools/NetBpm.SdkGen}</p>
 */
public enum TimelineEntryKind {
    INSTANCE_STARTED,
    TASK_CREATED,
    TASK_COMPLETED,
    TASK_CLAIMED,
    TASK_UNCLAIMED,
    TASK_TRANSFERRED,
    INSTANCE_SUSPENDED,
    INSTANCE_RESUMED,
    INSTANCE_TERMINATED,
    INSTANCE_COMPLETED,
    COMMENT,
    PARAMETERS_UPDATED,
    INSTANCE_JUMPED,

    // 兜底：服务端新增取值时落到这里，而不是让反序列化抛异常。
    // 需要开启 DeserializationFeature.READ_UNKNOWN_ENUM_VALUES_USING_DEFAULT_VALUE。
    @JsonEnumDefaultValue
    UNKNOWN
}
