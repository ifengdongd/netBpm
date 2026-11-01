import { http, toQueryString } from './client';

/**
 * 审批语义字典（`/admin/v1/dictionaries`）。
 *
 * 这一层解决的是「业务系统不该按 1/2/3 猜结论」：
 * BPMN 里声明的 `optionId` 是流程私有的，平台字典把它翻译成
 * `semantic`（跨流程稳定：同意 / 拒绝 / 退回 / 转办 / 自定义）+ `outcomeClass`（统计口径），
 * 调用方只认后两个，流程随便加选项都不会破坏集成代码。
 */

/** 语义（与后端 `ApprovalSemantic` 的 SCREAMING_SNAKE_CASE 线上值一致）。 */
export const APPROVAL_SEMANTICS = [
  { value: 'APPROVE', label: '同意 / 通过' },
  { value: 'REJECT', label: '不同意 / 拒绝' },
  { value: 'RETURN', label: '退回重办' },
  { value: 'DELEGATE', label: '转办 / 移交' },
  { value: 'CUSTOM', label: '流程自定义' },
  { value: 'SETTLED', label: '办结（无审批结论）' },
] as const;

/** 统计口径（通过率的分母怎么算由它决定）。 */
export const APPROVAL_OUTCOME_CLASSES = [
  { value: 'POSITIVE', label: '正向（计入通过率）' },
  { value: 'NEGATIVE', label: '负向（计入驳回 / 退回率）' },
  { value: 'NEUTRAL', label: '中性（不计入分子分母）' },
] as const;

/** 字典里的一条映射（`GET /admin/v1/dictionaries?processKey=`）。 */
export interface ApprovalDictionaryEntryView {
  tenantId: string;
  processKey: string;
  /** `null` = 流程级默认映射；非空 = 只对某个环节生效。 */
  nodeId: string | null;
  optionId: string;
  semantic: string;
  outcomeClass: string;
  label: string | null;
  requiresComment: boolean;
  requiresTargetNode: boolean;
  allowedTargetNodes: string[];
  attributes: Record<string, string>;
  createdAt: string;
  updatedAt: string;
}

/** BPMN 里声明的一个选项（字典的「可选范围」）。 */
export interface DeclaredApprovalOption {
  optionId: string;
  name: string | null;
}

export interface DeclaredNodeApprovalOptions {
  activityId: string;
  activityName: string | null;
  options: DeclaredApprovalOption[];
}

export interface DeclaredApprovalOptionsView {
  processKey: string;
  processDefinitionId: string;
  definitionVersion: number;
  nodes: DeclaredNodeApprovalOptions[];
}

export interface ApprovalDictionaryUpsertRequest {
  nodeId: string | null;
  semantic: string;
  label?: string | null;
  requiresComment?: boolean;
  requiresTargetNode?: boolean;
  allowedTargetNodes?: string[] | null;
  attributes?: Record<string, string> | null;
}

export const semanticsApi = {
  /** 某流程的字典映射（流程级 + 各环节级）。 */
  list: (processKey: string, signal?: AbortSignal) =>
    http.get<ApprovalDictionaryEntryView[]>(
      `/admin/v1/dictionaries${toQueryString({ processKey })}`,
      { signal },
    ),

  /** 当前生效版本在 BPMN 里声明的选项：配字典的「有据可依」。 */
  declared: (processKey: string, signal?: AbortSignal) =>
    http.get<DeclaredApprovalOptionsView>(
      `/admin/v1/dictionaries/${encodeURIComponent(processKey)}/declared`,
      { signal },
    ),

  /** 写映射（PUT 是全量覆盖 `nodeId` 行；不传 `nodeId` 就是流程级默认）。 */
  upsert: (processKey: string, optionId: string, request: ApprovalDictionaryUpsertRequest) =>
    http.put<ApprovalDictionaryEntryView>(
      `/admin/v1/dictionaries/${encodeURIComponent(processKey)}/options/${encodeURIComponent(optionId)}`,
      request,
    ),

  /** 删除映射：`nodeId` 走查询参数（同一个 optionId 可以同时有流程级与环节级两行）。 */
  remove: (processKey: string, optionId: string, nodeId: string | null) =>
    http.del<void>(
      `/admin/v1/dictionaries/${encodeURIComponent(processKey)}/options/${encodeURIComponent(optionId)}`
        + toQueryString({ nodeId: nodeId ?? undefined }),
    ),
};
