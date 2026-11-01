/**
 * NetBpm 平台业务面（`/workflow/v1`）的数据模型。
 *
 * 模型来自 OpenAPI 快照里「被业务面操作可达」的那部分 schema；`/api/**` 与 `/admin/v1` 的类型不在其中。
 *
 * 本文件由 `tools/NetBpm.SdkGen` 从 `contracts/openapi/workflow-v1.json`（OpenAPI 3.1.1）生成，**请勿手改**：改了会在 SdkGenerationTests 里红。
 * 重新生成：`dotnet run --project tools/NetBpm.SdkGen`
 */

// ── ApprovalOption ──

export interface ApprovalOption {
  allowedTargetNodes: string[] | null;
  attributes: { [key: string]: string } | null;
  commentPrompt: string | null;
  label: string;
  optionId: string;
  outcomeClass: ApprovalOutcomeClass;
  requiresComment: boolean;
  requiresTargetNode: boolean;
  semantic: ApprovalSemantic;
}

// ── ApprovalOutcomeClass ──

export type ApprovalOutcomeClass = "POSITIVE" | "NEGATIVE" | "NEUTRAL";

export const approvalOutcomeClassValues: readonly ApprovalOutcomeClass[] = [
  "POSITIVE",
  "NEGATIVE",
  "NEUTRAL",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseApprovalOutcomeClass(value: string | null | undefined): ApprovalOutcomeClass | undefined {
  return value != null && (approvalOutcomeClassValues as readonly string[]).includes(value)
    ? (value as ApprovalOutcomeClass)
    : undefined;
}

// ── ApprovalSemantic ──

export type ApprovalSemantic = "APPROVE" | "REJECT" | "RETURN" | "DELEGATE" | "CUSTOM" | "CLOSED";

export const approvalSemanticValues: readonly ApprovalSemantic[] = [
  "APPROVE",
  "REJECT",
  "RETURN",
  "DELEGATE",
  "CUSTOM",
  "CLOSED",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseApprovalSemantic(value: string | null | undefined): ApprovalSemantic | undefined {
  return value != null && (approvalSemanticValues as readonly string[]).includes(value)
    ? (value as ApprovalSemantic)
    : undefined;
}

// ── AttachmentRef ──

export interface AttachmentRef {
  contentType?: string | null;
  name: string;
  sha256?: string | null;
  size?: number | null;
  url: string;
}

// ── ClaimTaskRequest ──

export interface ClaimTaskRequest {
  expectedRevision?: number | null;
  unclaim?: boolean | null;
  userId?: string | null;
}

// ── ConclusionSource ──

export type ConclusionSource = "DECLARED" | "LAST_DECISION" | "FALLBACK";

export const conclusionSourceValues: readonly ConclusionSource[] = [
  "DECLARED",
  "LAST_DECISION",
  "FALLBACK",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseConclusionSource(value: string | null | undefined): ConclusionSource | undefined {
  return value != null && (conclusionSourceValues as readonly string[]).includes(value)
    ? (value as ConclusionSource)
    : undefined;
}

// ── CursorPageOfInstanceSummary ──

export interface CursorPageOfInstanceSummary {
  hasMore: boolean;
  items: InstanceSummary[];
  nextCursor: string | null;
}

// ── CursorPageOfProcessDescriptor ──

export interface CursorPageOfProcessDescriptor {
  hasMore: boolean;
  items: ProcessDescriptor[];
  nextCursor: string | null;
}

// ── CursorPageOfTaskSummary ──

export interface CursorPageOfTaskSummary {
  hasMore: boolean;
  items: TaskSummary[];
  nextCursor: string | null;
}

// ── Decision ──

export interface Decision {
  comment: string | null;
  decidedAt: string | null;
  optionId: string | null;
  semantic: ApprovalSemantic | null;
}

// ── FormField ──

export interface FormField {
  defaultValue: string | null;
  fieldId: string;
  label: string | null;
  options: FormOption[] | null;
  placeholder: string | null;
  readOnly: boolean;
  required: boolean;
  sortOrder: number;
  type: FormFieldType;
}

// ── FormFieldType ──

export type FormFieldType = "STRING" | "TEXT" | "LONG" | "DECIMAL" | "BOOLEAN" | "DATE" | "DATE_TIME" | "ENUM" | "FILE";

export const formFieldTypeValues: readonly FormFieldType[] = [
  "STRING",
  "TEXT",
  "LONG",
  "DECIMAL",
  "BOOLEAN",
  "DATE",
  "DATE_TIME",
  "ENUM",
  "FILE",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseFormFieldType(value: string | null | undefined): FormFieldType | undefined {
  return value != null && (formFieldTypeValues as readonly string[]).includes(value)
    ? (value as FormFieldType)
    : undefined;
}

// ── FormOption ──

export interface FormOption {
  label: string | null;
  value: string;
}

// ── FormSchema ──

export interface FormSchema {
  activityId: string | null;
  fields: FormField[];
  processKey: string;
}

// ── FormSnapshot ──

export interface FormSnapshot {
  activityName?: string | null;
  actorId?: string | null;
  attachments?: AttachmentRef[] | null;
  capturedAt: string;
  definitionVersion: number;
  instanceId?: string | null;
  optionId?: string | null;
  schema: FormSchema;
  taskId: string;
  values: { [key: string]: string };
}

// ── HistoryView ──

export interface HistoryView {
  businessKey: string | null;
  conclusion: InstanceConclusion | null;
  durationInMilliseconds: number | null;
  endedAt: string | null;
  entries: TimelineEntry[];
  instanceId: string;
  nextCursor: string | null;
  nodeOutcomes: NodeOutcome[];
  processKey: string;
  startedAt: string | null;
  startedBy: string | null;
  status: InstanceStatus;
}

// ── InstanceConclusion ──

export interface InstanceConclusion {
  label: string;
  optionId: string | null;
  semantic: ApprovalSemantic;
  source: ConclusionSource;
}

// ── InstanceParameter ──

export interface InstanceParameter {
  apiName: string;
  constraints: ParameterConstraints | null;
  declared: boolean;
  description: string | null;
  displayName: string | null;
  required: boolean;
  sortOrder: number;
  type: ParameterType;
  value: string | null;
  variableName: string | null;
  writableBy: string[] | null;
}

// ── InstanceParameterView ──

export interface InstanceParameterView {
  instanceId: string;
  parameters: InstanceParameter[];
  processDefinitionId: string | null;
  processKey: string;
  revision: number;
  schemaVersion: number;
}

// ── InstanceStatus ──

export type InstanceStatus = "RUNNING" | "COMPLETED" | "TERMINATED" | "SUSPENDED" | "FAILED";

export const instanceStatusValues: readonly InstanceStatus[] = [
  "RUNNING",
  "COMPLETED",
  "TERMINATED",
  "SUSPENDED",
  "FAILED",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseInstanceStatus(value: string | null | undefined): InstanceStatus | undefined {
  return value != null && (instanceStatusValues as readonly string[]).includes(value)
    ? (value as InstanceStatus)
    : undefined;
}

// ── InstanceSummary ──

export interface InstanceSummary {
  businessKey: string | null;
  conclusion: InstanceConclusion | null;
  currentActivityIds: string[];
  currentAssignees: string[];
  endedAt: string | null;
  instanceId: string;
  myRoles?: ParticipationRole[] | null;
  processKey: string;
  startedAt: string | null;
  startedBy: string | null;
  status: InstanceStatus;
  title: string | null;
}

// ── InstanceView ──

export interface InstanceView {
  businessKey: string | null;
  conclusion: InstanceConclusion | null;
  currentActivityIds: string[];
  definitionVersion: number;
  endedAt: string | null;
  instanceId: string;
  nodeOutcomes: NodeOutcome[];
  parameters: ParameterSnapshot;
  processDefinitionId: string;
  processKey: string;
  revision: number;
  startedAt: string | null;
  startedBy: string | null;
  status: InstanceStatus;
  tasks: TaskView[];
  title: string | null;
}

// ── NodeOutcome ──

export interface NodeOutcome {
  activityId: string;
  activityName: string | null;
  aggregation: NodeOutcomeAggregation;
  decidedAt: string | null;
  negative: number;
  pending: number;
  positive: number;
  satisfied: boolean;
  total: number;
}

// ── NodeOutcomeAggregation ──

export type NodeOutcomeAggregation = "SINGLE" | "ALL_APPROVED" | "ANY_APPROVED" | "MAJORITY";

export const nodeOutcomeAggregationValues: readonly NodeOutcomeAggregation[] = [
  "SINGLE",
  "ALL_APPROVED",
  "ANY_APPROVED",
  "MAJORITY",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseNodeOutcomeAggregation(value: string | null | undefined): NodeOutcomeAggregation | undefined {
  return value != null && (nodeOutcomeAggregationValues as readonly string[]).includes(value)
    ? (value as NodeOutcomeAggregation)
    : undefined;
}

// ── ParameterConstraints ──

export interface ParameterConstraints {
  enumValues: string[] | null;
  maxLength: number | null;
  maximum: number | null;
  minLength: number | null;
  minimum: number | null;
  pattern: string | null;
}

// ── ParameterDefinition ──

export interface ParameterDefinition {
  apiName: string;
  constraints: ParameterConstraints | null;
  defaultValue: string | null;
  description: string | null;
  displayName: string | null;
  required: boolean;
  sortOrder: number;
  type: ParameterType;
  variableName: string | null;
  visibleTo: string[] | null;
  writableBy: string[] | null;
}

// ── ParameterSchema ──

export interface ParameterSchema {
  parameters: ParameterDefinition[];
  processKey: string;
  reservedPrefixes: string[];
  version: number;
}

// ── ParameterSnapshot ──

export interface ParameterSnapshot {
  instanceId: string;
  revision: number;
  updatedAt: string | null;
  updatedBy: string | null;
  values: { [key: string]: string };
}

// ── ParameterType ──

export type ParameterType = "STRING" | "LONG" | "DECIMAL" | "BOOLEAN" | "DATE" | "DATE_TIME" | "ENUM" | "JSON";

export const parameterTypeValues: readonly ParameterType[] = [
  "STRING",
  "LONG",
  "DECIMAL",
  "BOOLEAN",
  "DATE",
  "DATE_TIME",
  "ENUM",
  "JSON",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseParameterType(value: string | null | undefined): ParameterType | undefined {
  return value != null && (parameterTypeValues as readonly string[]).includes(value)
    ? (value as ParameterType)
    : undefined;
}

// ── ParticipationRole ──

export type ParticipationRole = "STARTER" | "APPROVER" | "CANDIDATE" | "PARTICIPANT";

export const participationRoleValues: readonly ParticipationRole[] = [
  "STARTER",
  "APPROVER",
  "CANDIDATE",
  "PARTICIPANT",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseParticipationRole(value: string | null | undefined): ParticipationRole | undefined {
  return value != null && (participationRoleValues as readonly string[]).includes(value)
    ? (value as ParticipationRole)
    : undefined;
}

// ── PlatformFieldError ──

export interface PlatformFieldError {
  code: string;
  field: string;
  message: string;
}

// ── PlatformProblem ──

export interface PlatformProblem {
  code: string;
  detail: string | null;
  errors: PlatformFieldError[] | null;
  instance: string | null;
  status: number;
  title: string;
  traceId: string | null;
  type: string;
}

// ── ProcessCapabilities ──

export interface ProcessCapabilities {
  canReadDefinition: boolean;
  canReadDiagram: boolean;
  canStart: boolean;
  hasFormSchema: boolean;
  hasParameterSchema: boolean;
  supportedSemantics: ApprovalSemantic[];
}

// ── ProcessDefinitionInfo ──

export interface ProcessDefinitionInfo {
  checksum: string;
  deployedAt: string | null;
  processDefinitionId: string;
  processKey: string;
  suspended: boolean;
  version: number;
  xml: string | null;
}

// ── ProcessDescriptor ──

export interface ProcessDescriptor {
  canStart?: boolean;
  capabilities: ProcessCapabilities;
  category: string | null;
  definitionChecksum: string | null;
  deployedVersion: number;
  description: string | null;
  entryPoints: ProcessEntryPoint[];
  icon: string | null;
  labels: { [key: string]: string } | null;
  name: string;
  pageUrls: { [key: string]: string } | null;
  parameterSchemaVersion: number;
  processKey: string;
  status: ProcessStatus;
}

// ── ProcessEntryPoint ──

export interface ProcessEntryPoint {
  defaultParameters: { [key: string]: string } | null;
  key: string;
  name: string | null;
  startActivityId: string | null;
}

// ── ProcessFormSchema ──

export interface ProcessFormSchema {
  activities: FormSchema[];
  definitionVersion: number;
  processDefinitionId: string;
  processKey: string;
}

// ── ProcessStatus ──

export type ProcessStatus = "DRAFT" | "ACTIVE" | "SUSPENDED" | "RETIRED";

export const processStatusValues: readonly ProcessStatus[] = [
  "DRAFT",
  "ACTIVE",
  "SUSPENDED",
  "RETIRED",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseProcessStatus(value: string | null | undefined): ProcessStatus | undefined {
  return value != null && (processStatusValues as readonly string[]).includes(value)
    ? (value as ProcessStatus)
    : undefined;
}

// ── ProcessVersionInfo ──

export interface ProcessVersionInfo {
  checksum: string;
  deployedAt: string | null;
  isCurrent: boolean;
  processDefinitionId: string;
  suspended: boolean;
  version: number;
}

// ── StartInstanceRequest ──

export interface StartInstanceRequest {
  businessKey?: string | null;
  entryPointKey?: string | null;
  parameters?: { [key: string]: string } | null;
  processKey: string;
  title?: string | null;
}

// ── StartInstanceResult ──

export interface StartInstanceResult {
  businessKey: string | null;
  instance: InstanceView | null;
  instanceId: string;
  processKey: string;
}

// ── SubmitTaskRequest ──

export interface SubmitTaskRequest {
  attachments?: AttachmentRef[] | null;
  comment?: string | null;
  expectedRevision?: number | null;
  formValues?: { [key: string]: string } | null;
  optionId: string;
  parameters?: { [key: string]: string } | null;
  semantic?: ApprovalSemantic | null;
  targetNodeId?: string | null;
}

// ── SubmitTaskResult ──

export interface SubmitTaskResult {
  events: string[];
  instance: InstanceView;
  nextTasks: TaskView[];
  timelineDelta: TimelineEntry[];
}

// ── TaskSummary ──

export interface TaskSummary {
  activityName: string | null;
  assignee: string | null;
  businessKey: string | null;
  completedAt?: string | null;
  createdAt: string | null;
  decision?: Decision | null;
  dueAt: string | null;
  entryUrl: string | null;
  instanceId: string;
  processKey: string;
  processName: string;
  startedBy: string | null;
  summary: string | null;
  taskId: string;
  title: string | null;
}

// ── TaskView ──

export interface TaskView {
  activityId: string | null;
  activityName: string | null;
  assignee: string | null;
  candidateGroups: string[];
  candidateUsers: string[];
  createdAt: string | null;
  currentUserCanSubmit: boolean;
  definitionVersion?: number | null;
  dueAt: string | null;
  form: FormField[] | null;
  instanceId: string;
  options: ApprovalOption[];
  processDefinitionId?: string | null;
  processKey?: string | null;
  taskId: string;
}

// ── TerminateInstanceRequest ──

export interface TerminateInstanceRequest {
  expectedRevision?: number | null;
  reason: string;
}

// ── TimelineEntry ──

export interface TimelineEntry {
  activityId: string | null;
  activityName: string | null;
  actorCallerId: string | null;
  actorId: string | null;
  comment: string | null;
  formSnapshotTaskId: string | null;
  instanceId: string;
  kind: TimelineEntryKind;
  occurredAt: string;
  optionId: string | null;
  semantic: ApprovalSemantic | null;
  sequence: number;
  targetNodeId: string | null;
  taskId: string | null;
}

// ── TimelineEntryKind ──

export type TimelineEntryKind = "INSTANCE_STARTED" | "TASK_CREATED" | "TASK_COMPLETED" | "TASK_CLAIMED" | "TASK_UNCLAIMED" | "TASK_TRANSFERRED" | "INSTANCE_SUSPENDED" | "INSTANCE_RESUMED" | "INSTANCE_TERMINATED" | "INSTANCE_COMPLETED" | "COMMENT" | "PARAMETERS_UPDATED" | "INSTANCE_JUMPED";

export const timelineEntryKindValues: readonly TimelineEntryKind[] = [
  "INSTANCE_STARTED",
  "TASK_CREATED",
  "TASK_COMPLETED",
  "TASK_CLAIMED",
  "TASK_UNCLAIMED",
  "TASK_TRANSFERRED",
  "INSTANCE_SUSPENDED",
  "INSTANCE_RESUMED",
  "INSTANCE_TERMINATED",
  "INSTANCE_COMPLETED",
  "COMMENT",
  "PARAMETERS_UPDATED",
  "INSTANCE_JUMPED",
];

/** 服务端会新增枚举取值（§7.2 要求客户端容忍未知值）：用它兜底，别直接断言。 */
export function parseTimelineEntryKind(value: string | null | undefined): TimelineEntryKind | undefined {
  return value != null && (timelineEntryKindValues as readonly string[]).includes(value)
    ? (value as TimelineEntryKind)
    : undefined;
}

// ── TransferTaskRequest ──

export interface TransferTaskRequest {
  assignee: string;
  comment?: string | null;
}

