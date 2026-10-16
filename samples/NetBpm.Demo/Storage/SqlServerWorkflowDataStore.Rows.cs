using NetBpm.Demo.Common;

namespace NetBpm.Demo.Storage;

/// <summary>SqlServerWorkflowDataStore 的行映射（与 MySqlWorkflowDataStore 同一套列别名）。</summary>
public sealed partial class SqlServerWorkflowDataStore
{
    // ───────────────────────── 行映射 ─────────────────────────

    private sealed class InstanceRow
    {
        public long ApplyId { get; set; }

        public long BusinessId { get; set; }

        public string? BusinessDeptId { get; set; }

        public string ApplyEmpId { get; set; } = string.Empty;

        public string ApplyDeptId { get; set; } = string.Empty;

        public DateTime ApplyTime { get; set; }

        public string? WorkflowTypeId { get; set; }

        public string WorkflowInstanceId { get; set; } = string.Empty;

        public string StatusId { get; set; } = string.Empty;

        public string? CurrentStep { get; set; }

        public DateTime? LastAuditedTime { get; set; }

        public string? LastResult { get; set; }

        public string? LastComment { get; set; }
    }

    private sealed class InstanceSnapshotRow
    {
        public long TenantId { get; set; }

        public long ApplyId { get; set; }

        public DateTime ApplyTime { get; set; }

        public string? WorkflowTypeId { get; set; }

        public string? WorkflowVersion { get; set; }

        public string? Describe { get; set; }

        public string ApplyEmpId { get; set; } = string.Empty;

        public string ApplyDeptId { get; set; } = string.Empty;

        public long BusinessId { get; set; }

        public string? BusinessDeptId { get; set; }

        public string StatusId { get; set; } = string.Empty;

        public short NewestFlag { get; set; }

        public string? WorkflowInstanceId { get; set; }

        public DateTime? LastAuditedTime { get; set; }

        public string CreateEmpId { get; set; } = string.Empty;

        public DateTime CreateTime { get; set; }

        public string? CurrentStep { get; set; }

        public string? AssignEmp { get; set; }

        public string? ModifyEmpId { get; set; }

        public DateTime? ModifyTime { get; set; }

        public byte DeletedFlag { get; set; }

        public long? OriginalBusinessId { get; set; }

        public long? FlowFileId { get; set; }
    }

    private sealed class ParameterRow
    {
        public string WorkflowInstanceId { get; set; } = string.Empty;

        public string ParamName { get; set; } = string.Empty;

        public string? ParamValue { get; set; }
    }

    private sealed class DealHistoryRow
    {
        public long DealHistoryId { get; set; }

        public long? BusinessId { get; set; }

        public long? ApplyId { get; set; }

        public string? TaskNode { get; set; }

        public string? DealEmpId { get; set; }

        public DateTime? DealTime { get; set; }

        public string? DealResult { get; set; }

        public string? DealComment { get; set; }

        public string? FormKey { get; set; }

        /// <summary>来自 instances.status_id（join 出来）—— 判断这条流水是不是「让流程结束的那条」。</summary>
        public string? StatusId { get; set; }

        public string? DealEmpName { get; set; }

        public string? DealDomainAccount { get; set; }
    }

    private sealed class CurrentTaskRow
    {
        public long StepId { get; set; }

        public long? ApplyId { get; set; }

        public string? TaskId { get; set; }

        public string? TaskNode { get; set; }

        public string? TaskOwner { get; set; }

        public string? AssignEmpId { get; set; }

        public string? FormKey { get; set; }

        public DateTime CreateTime { get; set; }
    }

    private sealed class InstanceRef
    {
        public long ApplyId { get; set; }

        public long BusinessId { get; set; }
    }
}
