import { DeleteOutlined, ReloadOutlined } from '@ant-design/icons';
import {
  Alert,
  App,
  Button,
  Card,
  Checkbox,
  Col,
  Form,
  Input,
  InputNumber,
  Modal,
  Row,
  Select,
  Space,
  Statistic,
  Switch,
  Table,
  Tabs,
  Tag,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';

import { jobsApi, runtimeApi } from '../api';
import { ApiError, newIdempotencyKey } from '../api/client';
import type {
  HistoricProcessInstance,
  ProcessInstanceResponse,
  TaskResponse,
  TimerJobSummary,
  Variables,
} from '../api/types';
import InstanceDetailDrawer from '../components/InstanceDetailDrawer';
import TaskDetailDrawer from '../components/TaskDetailDrawer';
import { usePagedList } from '../hooks/usePagedList';
import { formatTime } from '../utils';
import { buildHistoricColumns, buildInstanceColumns, buildTaskColumns } from './monitoring/columns';

/**
 * 管理端点只在 Admin 宿主提供（/api/admin/**）。
 * Rest 宿主（默认 5080）只有 designer / runtime / identity，打到它会得到 404。
 */
const WRONG_HOST_HINT =
  '当前后端不是 Admin 宿主（缺少 /api/admin 端点），请把控制台指向 NetBpm.Admin（默认 5081）';

/** 404 = 端点不存在。用于识别「控制台代理指错宿主」，而不是「确实没有数据」。 */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

interface TaskActionState {
  mode: 'assignee' | 'complete';
  task: TaskResponse;
}

/** 运行实例筛选条件（对应 ProcessInstanceQuery 的可用字段）。 */
interface InstanceFilter {
  processDefinitionKey?: string;
  businessKey?: string;
  startUserId?: string;
}

/** 待办筛选条件（后端要求至少给 assignee / candidateUser / processInstanceId 之一）。 */
interface TaskFilter {
  assignee?: string;
  candidateUser?: string;
  processInstanceId?: string;
  taskDefinitionKey?: string;
}

/** 历史实例状态筛选（映射到 finished / deleted 两个后端条件）。 */
type HistoricState = 'finished' | 'running' | 'deleted';

interface HistoricFilter {
  processDefinitionKey?: string;
  businessKey?: string;
  startUserId?: string;
  state?: HistoricState;
}

/** 定时作业筛选（dueBefore 为 ISO 时间字符串）。 */
interface JobFilter {
  processInstanceId?: string;
  processDefinitionId?: string;
  dueBefore?: string;
}

export default function MonitoringPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [actionForm] = Form.useForm<{ assignee?: string; variables?: string }>();

  // 自动刷新：开启后统计 + 三个列表每 30s 轮询（抽屉详情跟随 invalidate 联动，不单独轮询）
  const [autoRefresh, setAutoRefresh] = useState(false);
  const refetchInterval = autoRefresh ? 30_000 : false;

  // 三个列表的服务端筛选：draft 是输入框里的值，filter 是点「查询」后生效的值。
  // 输入即查询会每敲一个字发一次请求，也容易把半截输入当成条件误查。
  const [instanceDraft, setInstanceDraft] = useState<InstanceFilter>({});
  const [instanceFilter, setInstanceFilter] = useState<InstanceFilter>({});
  const [taskDraft, setTaskDraft] = useState<TaskFilter>({});
  const [taskFilter, setTaskFilter] = useState<TaskFilter>({});
  const [historicDraft, setHistoricDraft] = useState<HistoricFilter>({});
  const [historicFilter, setHistoricFilter] = useState<HistoricFilter>({});
  const [jobDraft, setJobDraft] = useState<JobFilter>({});
  const [jobFilter, setJobFilter] = useState<JobFilter>({});

  // 统计
  const statsQuery = useQuery({
    queryKey: ['runtime', 'task-stats'],
    queryFn: () => runtimeApi.taskStats({ timeZoneOffsetMinutes: -new Date().getTimezoneOffset() }),
    refetchInterval,
  });
  const stats = statsQuery.data ?? null;

  useEffect(() => {
    if (statsQuery.isError) {
      message.error(`加载待办统计失败：${(statsQuery.error as Error).message}`);
    }
  }, [statsQuery.isError, statsQuery.error, message]);

  // 三个列表共用 usePagedList：缓存、去重与失效联动由 queryClient 统一管理。
  // 条件进 queryKey：改条件即换缓存键，自动重新拉取且不会命中旧条件的缓存。
  const instanceList = usePagedList<ProcessInstanceResponse>(
    ['runtime', 'instances', instanceFilter],
    (page, signal) =>
      runtimeApi.instances(
        {
          ...instanceFilter,
          firstResult: (page.current - 1) * page.pageSize,
          maxResults: page.pageSize,
        },
        signal,
      ),
    (error) => message.error(`加载运行实例失败：${error.message}`),
    { refetchInterval },
  );

  const taskList = usePagedList<TaskResponse>(
    ['runtime', 'tasks', taskFilter],
    (page, signal) =>
      runtimeApi.tasks(
        {
          ...taskFilter,
          firstResult: (page.current - 1) * page.pageSize,
          maxResults: page.pageSize,
        },
        signal,
      ),
    (error) => message.error(`加载待办任务失败：${error.message}`),
    { refetchInterval },
  );

  const historicList = usePagedList<HistoricProcessInstance>(
    ['runtime', 'historic-instances', historicFilter],
    (page, signal) => {
      // 状态下拉映射成后端的 finished / deleted；其余字段原样透传
      const { state, ...rest } = historicFilter;

      return runtimeApi.historicInstances(
        {
          ...rest,
          finished: state === 'finished' ? true : state === 'running' ? false : undefined,
          deleted: state === 'deleted' ? true : undefined,
          firstResult: (page.current - 1) * page.pageSize,
          maxResults: page.pageSize,
        },
        signal,
      );
    },
    (error) => message.error(`加载历史实例失败：${error.message}`),
    { refetchInterval },
  );

  // 定时作业（边界定时器）：到期时间升序，运维用来判断「谁在等超时」
  const jobList = usePagedList<TimerJobSummary>(
    ['admin', 'jobs', jobFilter],
    (page, signal) =>
      jobsApi.list(
        {
          ...jobFilter,
          firstResult: (page.current - 1) * page.pageSize,
          maxResults: page.pageSize,
        },
        signal,
      ),
    (error) => {
      // 404 = 代理到了 Rest 宿主：面板内已有常驻 Alert 提示，这里不再弹 toast，
      // 否则 30s 自动刷新会反复打扰。
      if (isNotFound(error)) {
        return;
      }

      message.error(`加载定时作业失败：${error.message}`);
    },
    { refetchInterval },
  );

  const jobStats = useQuery({
    queryKey: ['admin', 'jobs-stats', jobFilter],
    queryFn: () =>
      jobsApi.stats({
        processInstanceId: jobFilter.processInstanceId,
        processDefinitionId: jobFilter.processDefinitionId,
      }),
    refetchInterval,
  });

  // 作业端点 404 说明控制台没有指向 Admin 宿主；派生自错误状态，恢复后提示自动消失。
  const jobHostMismatch = isNotFound(jobList.error) || isNotFound(jobStats.error);

  // 抽屉 / 弹窗
  const [searchParams, setSearchParams] = useSearchParams();
  // 支持 ?instanceId=xxx 深链：从「部署管理 → 发起实例」的成功提示跳过来时直接打开详情。
  // 地址栏是外部数据源，直接派生打开状态（不再用 effect 回写 state，避免级联渲染）；
  // 用户从列表手动点开的实例用独立 state 覆盖它。
  const instanceIdFromQuery = searchParams.get('instanceId');
  const [manualInstanceId, setManualInstanceId] = useState<string | null>(null);
  const detailInstanceId = manualInstanceId ?? instanceIdFromQuery;
  const [detailTaskId, setDetailTaskId] = useState<string | null>(null);
  const [taskAction, setTaskAction] = useState<TaskActionState | null>(null);
  const [actionSubmitting, setActionSubmitting] = useState(false);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);

  // 任务改期 / 改优先级（PATCH /tasks/{id}）
  const [editTask, setEditTask] = useState<TaskResponse | null>(null);
  const [editSubmitting, setEditSubmitting] = useState(false);
  const [editForm] = Form.useForm<{ dueDate?: string; priority?: number }>();

  // 定时作业改期 / 立即触发
  const [jobReschedule, setJobReschedule] = useState<TimerJobSummary | null>(null);
  const [jobSubmitting, setJobSubmitting] = useState(false);
  const [executingJobId, setExecutingJobId] = useState<string | null>(null);
  const [jobForm] = Form.useForm<{ dueDate?: string }>();

  // 批量改任务到期时间
  const [selectedTaskIds, setSelectedTaskIds] = useState<string[]>([]);
  const [batchDueOpen, setBatchDueOpen] = useState(false);
  const [batchDueSubmitting, setBatchDueSubmitting] = useState(false);
  const [batchDueForm] = Form.useForm<{ dueDate?: string; clearDueDate?: boolean }>();

  // 批量转办（清空办理人 = 退回候选池）
  const [batchAssigneeOpen, setBatchAssigneeOpen] = useState(false);
  const [batchAssigneeSubmitting, setBatchAssigneeSubmitting] = useState(false);
  const [batchAssigneeForm] = Form.useForm<{ assignee?: string; clearAssignee?: boolean }>();

  /** 关闭实例详情：同时清掉地址栏的 instanceId，避免刷新后又弹出来。 */
  const closeInstanceDetail = () => {
    setManualInstanceId(null);

    if (searchParams.has('instanceId')) {
      const next = new URLSearchParams(searchParams);
      next.delete('instanceId');
      setSearchParams(next, { replace: true });
    }
  };

  // 所有 runtime 查询（统计 / 三个列表 / 两个抽屉详情）统一失效刷新
  const refreshAll = () => {
    setSelectedIds([]);
    void queryClient.invalidateQueries({ queryKey: ['runtime'] });
  };

  // ─────────────────────────── 列表筛选 ───────────────────────────

  const hasInstanceFilter = hasTextFilter(instanceFilter);
  const hasTaskFilter = hasTextFilter(taskFilter);
  const hasHistoricFilter = hasTextFilter(historicFilter) || historicFilter.state !== undefined;
  const hasJobFilter = hasTextFilter(jobFilter);

  /** 应用筛选：条件生效 + 回到第一页（否则可能停在已不存在的页上）+ 清空跨页勾选。 */
  const applyInstanceFilter = () => {
    const next = normalizeFilters(instanceDraft);

    // 条件没变时按「刷新」语义处理：否则 queryKey 值相同，React Query 不会重新拉取
    if (isSameFilter(next, instanceFilter)) {
      instanceList.reload();
      return;
    }

    setInstanceFilter(next);
    setSelectedIds([]);

    if (instanceList.page.current !== 1) {
      instanceList.setPage({ ...instanceList.page, current: 1 });
    }
  };

  const resetInstanceFilter = () => {
    setInstanceDraft({});
    setInstanceFilter({});
    setSelectedIds([]);

    if (instanceList.page.current !== 1) {
      instanceList.setPage({ ...instanceList.page, current: 1 });
    }
  };

  const applyTaskFilter = () => {
    const next = normalizeFilters(taskDraft);

    if (isSameFilter(next, taskFilter)) {
      taskList.reload();
      return;
    }

    setTaskFilter(next);

    if (taskList.page.current !== 1) {
      taskList.setPage({ ...taskList.page, current: 1 });
    }
  };

  const resetTaskFilter = () => {
    setTaskDraft({});
    setTaskFilter({});

    if (taskList.page.current !== 1) {
      taskList.setPage({ ...taskList.page, current: 1 });
    }
  };

  const applyHistoricFilter = () => {
    const next = normalizeFilters(historicDraft);

    if (isSameFilter(next, historicFilter)) {
      historicList.reload();
      return;
    }

    setHistoricFilter(next);

    if (historicList.page.current !== 1) {
      historicList.setPage({ ...historicList.page, current: 1 });
    }
  };

  const resetHistoricFilter = () => {
    setHistoricDraft({});
    setHistoricFilter({});

    if (historicList.page.current !== 1) {
      historicList.setPage({ ...historicList.page, current: 1 });
    }
  };

  const applyJobFilter = () => {
    // datetime-local 是本地时间；提交前转成带时区的 ISO，避免服务端按 UTC 解读
    const dueBefore = toIsoOrUndefined(jobDraft.dueBefore);
    const next = normalizeFilters({ ...jobDraft, dueBefore });

    if (isSameFilter(next, jobFilter)) {
      jobList.reload();
      return;
    }

    setJobFilter(next);

    if (jobList.page.current !== 1) {
      jobList.setPage({ ...jobList.page, current: 1 });
    }
  };

  const resetJobFilter = () => {
    setJobDraft({});
    setJobFilter({});

    if (jobList.page.current !== 1) {
      jobList.setPage({ ...jobList.page, current: 1 });
    }
  };

  const openJobReschedule = (job: TimerJobSummary) => {
    jobForm.setFieldsValue({ dueDate: toLocalDateTime(job.dueDate) });
    setJobReschedule(job);
  };

  const submitJobReschedule = async () => {
    if (jobReschedule === null) {
      return;
    }

    let values: { dueDate?: string };

    try {
      values = await jobForm.validateFields();
    } catch {
      return;
    }

    const dueDate = toIsoOrUndefined(values.dueDate);

    if (dueDate === undefined) {
      message.error('请填写新的到期时间');
      return;
    }

    setJobSubmitting(true);

    try {
      await jobsApi.reschedule(jobReschedule.id, dueDate);
      message.success('作业已改期');
      setJobReschedule(null);
      void queryClient.invalidateQueries({ queryKey: ['admin', 'jobs'] });
      void queryClient.invalidateQueries({ queryKey: ['admin', 'jobs-stats'] });
    } catch (error) {
      message.error(`改期失败：${(error as Error).message}`);
    } finally {
      setJobSubmitting(false);
    }
  };

  /** 批量改到期时间：逐项执行，成功/失败条数如实反馈（单项失败不影响其它项）。 */
  const submitBatchDueDate = async () => {
    let values: { dueDate?: string; clearDueDate?: boolean };

    try {
      values = await batchDueForm.validateFields();
    } catch {
      return;
    }

    const dueDate = toIsoOrUndefined(values.dueDate);

    if (dueDate === undefined && values.clearDueDate !== true) {
      message.error('请填写新的到期时间，或勾选「清空到期时间」');
      return;
    }

    setBatchDueSubmitting(true);

    try {
      const result = await runtimeApi.batchSetTaskDueDate(
        selectedTaskIds,
        dueDate,
        values.clearDueDate === true,
      );

      if (result.failed > 0) {
        message.warning(`批量改期完成：成功 ${result.succeeded} 条，失败 ${result.failed} 条`);
      } else {
        message.success(`批量改期完成：${result.succeeded} 条`);
      }

      setBatchDueOpen(false);
      setSelectedTaskIds([]);
      batchDueForm.resetFields();
      void queryClient.invalidateQueries({ queryKey: ['runtime'] });
    } catch (error) {
      message.error(`批量改期失败：${(error as Error).message}`);
    } finally {
      setBatchDueSubmitting(false);
    }
  };

  /** 批量转办：逐项执行；传空办理人表示退回候选池（清空 assignee）。 */
  const submitBatchAssignee = async () => {
    let values: { assignee?: string; clearAssignee?: boolean };

    try {
      values = await batchAssigneeForm.validateFields();
    } catch {
      return;
    }

    const assignee = values.assignee?.trim();

    if (values.clearAssignee !== true && (assignee === undefined || assignee.length === 0)) {
      message.error('请填写新的办理人，或勾选「退回候选池」');
      return;
    }

    setBatchAssigneeSubmitting(true);

    try {
      const result = await runtimeApi.batchSetTaskAssignee(
        selectedTaskIds,
        values.clearAssignee === true ? undefined : assignee,
      );

      if (result.failed > 0) {
        message.warning(`批量转办完成：成功 ${result.succeeded} 条，失败 ${result.failed} 条`);
      } else {
        message.success(`批量转办完成：${result.succeeded} 条`);
      }

      setBatchAssigneeOpen(false);
      setSelectedTaskIds([]);
      batchAssigneeForm.resetFields();
      void queryClient.invalidateQueries({ queryKey: ['runtime'] });
    } catch (error) {
      message.error(`批量转办失败：${(error as Error).message}`);
    } finally {
      setBatchAssigneeSubmitting(false);
    }
  };

  /** 立即触发定时作业：不等到期，走与轮询相同的抢占语义（重复点击只有一个生效）。 */
  const executeJob = async (job: TimerJobSummary) => {
    setExecutingJobId(job.id);

    try {
      await jobsApi.execute(job.id);
      message.success('已触发定时作业');
      void queryClient.invalidateQueries({ queryKey: ['admin', 'jobs'] });
      void queryClient.invalidateQueries({ queryKey: ['admin', 'jobs-stats'] });
      void queryClient.invalidateQueries({ queryKey: ['runtime'] });
    } catch (error) {
      message.error(`触发失败：${(error as Error).message}`);
    } finally {
      setExecutingJobId(null);
    }
  };

  // ─────────────────────────── 实例操作 ───────────────────────────

  const toggleInstanceSuspension = async (instance: ProcessInstanceResponse) => {
    try {
      if (instance.isSuspended) {
        await runtimeApi.activateInstance(instance.id);
        message.success('已激活实例');
      } else {
        await runtimeApi.suspendInstance(instance.id);
        message.success('已挂起实例');
      }

      refreshAll();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    }
  };

  const removeInstance = async (instance: ProcessInstanceResponse) => {
    try {
      await runtimeApi.removeInstance(instance.id, '管理端删除');
      message.success('已删除实例');
      refreshAll();
    } catch (error) {
      message.error(`删除实例失败：${(error as Error).message}`);
    }
  };

  const batchDelete = async () => {
    if (selectedIds.length === 0) {
      message.warning('请先勾选要删除的实例');
      return;
    }

    try {
      const result = await runtimeApi.batchDeleteInstances(selectedIds, '管理端批量删除');
      message.success(`批量删除完成：成功 ${result.succeeded} 条，失败 ${result.failed} 条`);
      refreshAll();
    } catch (error) {
      message.error(`批量删除失败：${(error as Error).message}`);
    }
  };

  /** 批量挂起 / 激活（逐项执行；失败项在提示里给条数，详情看审计）。 */
  const batchSetSuspension = async (suspend: boolean) => {
    if (selectedIds.length === 0) {
      message.warning(`请先勾选要${suspend ? '挂起' : '激活'}的实例`);
      return;
    }

    try {
      const result = suspend
        ? await runtimeApi.batchSuspendInstances(selectedIds)
        : await runtimeApi.batchActivateInstances(selectedIds);

      message.success(
        `批量${suspend ? '挂起' : '激活'}完成：成功 ${result.succeeded} 条，失败 ${result.failed} 条`,
      );
      refreshAll();
    } catch (error) {
      message.error(`批量${suspend ? '挂起' : '激活'}失败：${(error as Error).message}`);
    }
  };

  // ─────────────────────────── 任务操作 ───────────────────────────

  const claimTask = async (task: TaskResponse) => {
    try {
      await runtimeApi.claimTask(task.id);
      message.success('已认领任务');
      refreshAll();
    } catch (error) {
      message.error(`认领失败：${(error as Error).message}`);
    }
  };

  const unclaimTask = async (task: TaskResponse) => {
    try {
      await runtimeApi.unclaimTask(task.id);
      message.success('已取消认领');
      refreshAll();
    } catch (error) {
      message.error(`取消认领失败：${(error as Error).message}`);
    }
  };

  const openTaskEdit = (task: TaskResponse) => {
    editForm.setFieldsValue({
      dueDate: toLocalDateTime(task.dueDate),
      priority: task.priority,
    });
    setEditTask(task);
  };

  const submitTaskEdit = async () => {
    if (editTask === null) {
      return;
    }

    let values: { dueDate?: string; priority?: number };

    try {
      values = await editForm.validateFields();
    } catch {
      return;
    }

    const dueDate = values.dueDate ? new Date(values.dueDate) : null;

    if (dueDate !== null && Number.isNaN(dueDate.getTime())) {
      message.error('到期时间格式不正确');
      return;
    }

    setEditSubmitting(true);

    try {
      await runtimeApi.updateTask(editTask.id, {
        dueDate: dueDate?.toISOString(),
        clearDueDate: dueDate === null,
        priority: values.priority,
      });
      message.success('任务已更新');
      setEditTask(null);
      refreshAll();
    } catch (error) {
      message.error(`更新任务失败：${(error as Error).message}`);
    } finally {
      setEditSubmitting(false);
    }
  };

  const openTaskAction = (mode: TaskActionState['mode'], task: TaskResponse) => {
    actionForm.resetFields();
    setTaskAction({ mode, task });
  };

  const submitTaskAction = async () => {
    if (taskAction === null) {
      return;
    }

    let values: { assignee?: string; variables?: string };

    try {
      values = await actionForm.validateFields();
    } catch {
      // 字段校验失败时 antd 已就地展示错误，无需额外提示
      return;
    }

    setActionSubmitting(true);

    try {
      if (taskAction.mode === 'assignee') {
        await runtimeApi.setTaskAssignee(taskAction.task.id, values.assignee?.trim() ?? '');
        message.success('已转办');
      } else {
        await runtimeApi.completeTask(
          taskAction.task.id,
          parseVariables(values.variables),
          newIdempotencyKey(),
        );
        message.success('已完成任务');
      }

      setTaskAction(null);
      refreshAll();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    } finally {
      setActionSubmitting(false);
    }
  };

  // ─────────────────────────── 表格列（定义见 monitoring/columns.tsx）───────────────────────────

  const instanceColumns = buildInstanceColumns({
    onDetail: setManualInstanceId,
    onToggleSuspension: (instance) => void toggleInstanceSuspension(instance),
    onRemove: (instance) => void removeInstance(instance),
  });

  const taskColumns = buildTaskColumns({
    onDetail: setDetailTaskId,
    onClaim: (task) => void claimTask(task),
    onUnclaim: (task) => void unclaimTask(task),
    onEdit: openTaskEdit,
    onOpenAction: (mode, task) => openTaskAction(mode, task),
  });

  const historicColumns = buildHistoricColumns({
    onDetail: setManualInstanceId,
  });

  const jobColumns: ColumnsType<TimerJobSummary> = [
    { title: '到期时间', dataIndex: 'dueDate', key: 'dueDate', width: 190, render: (v: string) => formatTime(v) },
    { title: '流程实例', dataIndex: 'processInstanceId', key: 'processInstanceId', ellipsis: true },
    { title: '节点', dataIndex: 'activityId', key: 'activityId', width: 160 },
    { title: '边界事件', dataIndex: 'boundaryEventId', key: 'boundaryEventId', width: 160 },
    { title: '创建时间', dataIndex: 'createTime', key: 'createTime', width: 190, render: (v: string) => formatTime(v) },
    {
      title: '操作',
      key: 'actions',
      width: 160,
      render: (_, record) => (
        <Space size={4}>
          <Button size="small" onClick={() => openJobReschedule(record)}>
            改期
          </Button>
          <Button
            size="small"
            loading={executingJobId === record.id}
            disabled={executingJobId !== null && executingJobId !== record.id}
            onClick={() => void executeJob(record)}
          >
            立即触发
          </Button>
        </Space>
      ),
    },
  ];

  const tabItems = [
    {
      key: 'instances',
      label: `运行实例（${instanceList.total}）`,
      children: (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <FilterBar
            active={hasInstanceFilter}
            onSearch={applyInstanceFilter}
            onReset={resetInstanceFilter}
          >
            <Input
              allowClear
              placeholder="流程 Key"
              style={{ width: 160 }}
              value={instanceDraft.processDefinitionKey ?? ''}
              onChange={(event) => setInstanceDraft({ ...instanceDraft, processDefinitionKey: event.target.value })}
              onPressEnter={applyInstanceFilter}
            />
            <Input
              allowClear
              placeholder="业务主键"
              style={{ width: 180 }}
              value={instanceDraft.businessKey ?? ''}
              onChange={(event) => setInstanceDraft({ ...instanceDraft, businessKey: event.target.value })}
              onPressEnter={applyInstanceFilter}
            />
            <Input
              allowClear
              placeholder="发起人"
              style={{ width: 140 }}
              value={instanceDraft.startUserId ?? ''}
              onChange={(event) => setInstanceDraft({ ...instanceDraft, startUserId: event.target.value })}
              onPressEnter={applyInstanceFilter}
            />
          </FilterBar>
          <Space wrap>
            <Button disabled={selectedIds.length === 0} onClick={() => void batchSetSuspension(true)}>
              批量挂起（{selectedIds.length}）
            </Button>
            <Button disabled={selectedIds.length === 0} onClick={() => void batchSetSuspension(false)}>
              批量激活
            </Button>
            <Button danger icon={<DeleteOutlined />} disabled={selectedIds.length === 0} onClick={() => void batchDelete()}>
              批量删除选中（{selectedIds.length}）
            </Button>
          </Space>
          <Table<ProcessInstanceResponse>
            rowKey="id"
            columns={instanceColumns}
            dataSource={instanceList.items}
            loading={instanceList.loading}
            rowSelection={{ selectedRowKeys: selectedIds, onChange: (keys) => setSelectedIds(keys as string[]) }}
            pagination={instanceList.pagination}
          />
        </Space>
      ),
    },
    {
      key: 'tasks',
      label: `待办任务（${taskList.total}）`,
      children: (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <FilterBar active={hasTaskFilter} onSearch={applyTaskFilter} onReset={resetTaskFilter}>
            <Input
              allowClear
              placeholder="办理人"
              style={{ width: 140 }}
              value={taskDraft.assignee ?? ''}
              onChange={(event) => setTaskDraft({ ...taskDraft, assignee: event.target.value })}
              onPressEnter={applyTaskFilter}
            />
            <Input
              allowClear
              placeholder="候选人"
              style={{ width: 140 }}
              value={taskDraft.candidateUser ?? ''}
              onChange={(event) => setTaskDraft({ ...taskDraft, candidateUser: event.target.value })}
              onPressEnter={applyTaskFilter}
            />
            <Input
              allowClear
              placeholder="流程实例 ID"
              style={{ width: 220 }}
              value={taskDraft.processInstanceId ?? ''}
              onChange={(event) => setTaskDraft({ ...taskDraft, processInstanceId: event.target.value })}
              onPressEnter={applyTaskFilter}
            />
            <Input
              allowClear
              placeholder="节点 Key（可选）"
              title="节点 Key 需配合办理人 / 候选人 / 实例 ID 使用：后端要求至少给出其中一个身份或实例条件。"
              style={{ width: 170 }}
              value={taskDraft.taskDefinitionKey ?? ''}
              onChange={(event) => setTaskDraft({ ...taskDraft, taskDefinitionKey: event.target.value })}
              onPressEnter={applyTaskFilter}
            />
          </FilterBar>
          <Space wrap>
            <Button disabled={selectedTaskIds.length === 0} onClick={() => setBatchDueOpen(true)}>
              批量改到期时间（{selectedTaskIds.length}）
            </Button>
            <Button disabled={selectedTaskIds.length === 0} onClick={() => setBatchAssigneeOpen(true)}>
              批量转办（{selectedTaskIds.length}）
            </Button>
          </Space>
          <Table<TaskResponse>
            rowKey="id"
            columns={taskColumns}
            dataSource={taskList.items}
            loading={taskList.loading}
            rowSelection={{
              selectedRowKeys: selectedTaskIds,
              onChange: (keys) => setSelectedTaskIds(keys as string[]),
            }}
            pagination={taskList.pagination}
          />
        </Space>
      ),
    },
    {
      key: 'historic',
      label: `历史实例（${historicList.total}）`,
      children: (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <FilterBar
            active={hasHistoricFilter}
            onSearch={applyHistoricFilter}
            onReset={resetHistoricFilter}
          >
            <Input
              allowClear
              placeholder="流程 Key"
              style={{ width: 160 }}
              value={historicDraft.processDefinitionKey ?? ''}
              onChange={(event) => setHistoricDraft({ ...historicDraft, processDefinitionKey: event.target.value })}
              onPressEnter={applyHistoricFilter}
            />
            <Input
              allowClear
              placeholder="业务主键"
              style={{ width: 180 }}
              value={historicDraft.businessKey ?? ''}
              onChange={(event) => setHistoricDraft({ ...historicDraft, businessKey: event.target.value })}
              onPressEnter={applyHistoricFilter}
            />
            <Input
              allowClear
              placeholder="发起人"
              style={{ width: 140 }}
              value={historicDraft.startUserId ?? ''}
              onChange={(event) => setHistoricDraft({ ...historicDraft, startUserId: event.target.value })}
              onPressEnter={applyHistoricFilter}
            />
            <Select<HistoricState>
              allowClear
              placeholder="状态"
              style={{ width: 130 }}
              value={historicDraft.state}
              onChange={(value) => setHistoricDraft({ ...historicDraft, state: value })}
              options={[
                { value: 'running', label: '未结束' },
                { value: 'finished', label: '已结束' },
                { value: 'deleted', label: '已删除' },
              ]}
            />
          </FilterBar>
          <Table<HistoricProcessInstance>
            rowKey="processInstanceId"
            columns={historicColumns}
            dataSource={historicList.items}
            loading={historicList.loading}
            pagination={historicList.pagination}
          />
        </Space>
      ),
    },
    {
      key: 'jobs',
      label: `定时作业（${jobStats.data?.total ?? jobList.total}）`,
      children: (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          {jobHostMismatch && (
            <Alert
              type="error"
              showIcon
              message={WRONG_HOST_HINT}
              description="定时作业属于管理端点（/api/admin/jobs）。Rest 宿主（默认 5080）只有 designer / runtime / identity，请改用 NETBPM_API_TARGET=http://localhost:5081 指向 NetBpm.Admin 后重启控制台（或刷新页面）。"
            />
          )}
          <FilterBar active={hasJobFilter} onSearch={applyJobFilter} onReset={resetJobFilter}>
            <Input
              allowClear
              placeholder="流程实例 ID"
              style={{ width: 260 }}
              value={jobDraft.processInstanceId ?? ''}
              onChange={(event) => setJobDraft({ ...jobDraft, processInstanceId: event.target.value })}
              onPressEnter={applyJobFilter}
            />
            <Input
              allowClear
              placeholder="流程定义 ID"
              style={{ width: 260 }}
              value={jobDraft.processDefinitionId ?? ''}
              onChange={(event) => setJobDraft({ ...jobDraft, processDefinitionId: event.target.value })}
              onPressEnter={applyJobFilter}
            />
            <Input
              allowClear
              type="datetime-local"
              title="只看该时刻之前到期的作业"
              style={{ width: 210 }}
              value={jobDraft.dueBefore ?? ''}
              onChange={(event) => setJobDraft({ ...jobDraft, dueBefore: event.target.value })}
            />
          </FilterBar>
          {/* 宿主不对时统计也是 0，显示「共 0 条」会误导，直接不渲染 */}
          {!jobHostMismatch && (
            <Typography.Text type="secondary">
              共 {jobStats.data?.total ?? 0} 条 · 已到期 {jobStats.data?.overdue ?? 0} 条 · 1 小时内{' '}
              {jobStats.data?.dueWithinHour ?? 0} 条 · 最近到期{' '}
              {formatTime(jobStats.data?.nextDueAt)}
            </Typography.Text>
          )}
          <Table<TimerJobSummary>
            rowKey="id"
            columns={jobColumns}
            dataSource={jobList.items}
            loading={jobList.loading}
            pagination={jobList.pagination}
          />
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Space style={{ width: '100%', justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">
          运行实例、待办任务与历史实例的实时监控；支持实例挂起/激活/跳转、任务认领/转办/完成。
        </Typography.Text>
        <Space>
          <Space size={4}>
            <Switch checked={autoRefresh} onChange={setAutoRefresh} size="small" />
            <Typography.Text type={autoRefresh ? undefined : 'secondary'} style={{ fontSize: 13 }}>
              自动刷新（30s）
            </Typography.Text>
          </Space>
          <Button icon={<ReloadOutlined />} onClick={refreshAll}>
            刷新
          </Button>
        </Space>
      </Space>

      <Row gutter={16}>
        <Col span={6}>
          <Card>
            <Statistic title="待办总数" value={stats?.total ?? 0} />
          </Card>
        </Col>
        <Col span={6}>
          <Card>
            <Statistic
              title="已逾期"
              value={stats?.overdue ?? 0}
              valueStyle={{ color: stats && stats.overdue > 0 ? '#cf1322' : undefined }}
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card>
            <Statistic title="今日到期" value={stats?.dueToday ?? 0} />
          </Card>
        </Col>
        <Col span={6}>
          <Card title="按流程分布">
            <Space direction="vertical" size={4} style={{ width: '100%' }}>
              {stats && Object.keys(stats.byDefinition).length > 0 ? (
                Object.entries(stats.byDefinition).map(([key, count]) => (
                  <Space key={key} style={{ justifyContent: 'space-between', width: '100%' }}>
                    <Tag>{key}</Tag>
                    <Typography.Text strong>{count}</Typography.Text>
                  </Space>
                ))
              ) : (
                <Typography.Text type="secondary">无待办</Typography.Text>
              )}
            </Space>
          </Card>
        </Col>
      </Row>

      <Tabs items={tabItems} />

      <InstanceDetailDrawer
        instanceId={detailInstanceId}
        open={detailInstanceId !== null}
        onClose={closeInstanceDetail}
        onChanged={refreshAll}
      />

      <TaskDetailDrawer
        taskId={detailTaskId}
        open={detailTaskId !== null}
        onClose={() => setDetailTaskId(null)}
        onOpenInstance={(instanceId) => {
          // 两个抽屉不同时叠着：先关任务详情，再打开实例详情
          setDetailTaskId(null);
          setManualInstanceId(instanceId);
        }}
      />

      <Modal
        title={taskAction?.mode === 'assignee' ? `转办任务「${taskAction.task.name ?? taskAction.task.id}」` : `完成任务「${taskAction?.task.name ?? taskAction?.task.id ?? ''}」`}
        open={taskAction !== null}
        confirmLoading={actionSubmitting}
        onOk={() => void submitTaskAction()}
        onCancel={() => setTaskAction(null)}
        okText={taskAction?.mode === 'assignee' ? '转办' : '完成'}
        cancelText="取消"
        destroyOnClose
      >
        <Form form={actionForm} layout="vertical">
          {taskAction?.mode === 'assignee' ? (
            <Form.Item name="assignee" label="新办理人（留空表示退回未分配）">
              <Input placeholder="如 zhangsan" autoComplete="off" />
            </Form.Item>
          ) : (
            <Form.Item
              name="variables"
              label="随完成写入的变量（JSON 对象，可留空）"
              rules={[{ validator: validateVariables }]}
            >
              <Input.TextArea rows={5} placeholder='{ "approved": true, "comment": "同意" }' />
            </Form.Item>
          )}
        </Form>
      </Modal>

      <Modal
        title={editTask ? `修改任务「${editTask.name ?? editTask.id}」` : ''}
        open={editTask !== null}
        confirmLoading={editSubmitting}
        okText="保存"
        cancelText="取消"
        onOk={() => void submitTaskEdit()}
        onCancel={() => setEditTask(null)}
        destroyOnClose
      >
        <Form form={editForm} layout="vertical" preserve={false}>
          <Form.Item
            name="dueDate"
            label="到期时间"
            extra="按本地时区填写；留空表示清除到期时间。"
          >
            <Input type="datetime-local" />
          </Form.Item>
          <Form.Item name="priority" label="优先级（0-100）">
            <InputNumber min={0} max={100} style={{ width: '100%' }} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title={
          jobReschedule
            ? `作业改期（${jobReschedule.processInstanceId} / ${jobReschedule.boundaryEventId}）`
            : ''
        }
        open={jobReschedule !== null}
        confirmLoading={jobSubmitting}
        okText="保存"
        cancelText="取消"
        onOk={() => void submitJobReschedule()}
        onCancel={() => setJobReschedule(null)}
        destroyOnClose
      >
        <Form form={jobForm} layout="vertical" preserve={false}>
          <Form.Item
            name="dueDate"
            label="新的到期时间"
            extra="按本地时区填写；到点后由宿主的定时轮询触发边界事件。"
            rules={[{ required: true, message: '请选择到期时间' }]}
          >
            <Input type="datetime-local" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title={`批量改到期时间（${selectedTaskIds.length} 条）`}
        open={batchDueOpen}
        confirmLoading={batchDueSubmitting}
        okText="保存"
        cancelText="取消"
        onOk={() => void submitBatchDueDate()}
        onCancel={() => setBatchDueOpen(false)}
        destroyOnClose
      >
        <Form form={batchDueForm} layout="vertical" preserve={false}>
          <Form.Item name="clearDueDate" valuePropName="checked">
            <Checkbox>清空到期时间（忽略下面的时间）</Checkbox>
          </Form.Item>
          <Form.Item
            name="dueDate"
            label="新的到期时间"
            extra="按本地时区填写；逐项执行，单条失败不影响其它条。"
          >
            <Input type="datetime-local" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title={`批量转办（${selectedTaskIds.length} 条）`}
        open={batchAssigneeOpen}
        confirmLoading={batchAssigneeSubmitting}
        okText="保存"
        cancelText="取消"
        onOk={() => void submitBatchAssignee()}
        onCancel={() => setBatchAssigneeOpen(false)}
        destroyOnClose
      >
        <Form form={batchAssigneeForm} layout="vertical" preserve={false}>
          <Form.Item name="clearAssignee" valuePropName="checked">
            <Checkbox>退回候选池（清空办理人）</Checkbox>
          </Form.Item>
          <Form.Item
            name="assignee"
            label="新的办理人"
            extra="填写用户 id；逐项执行，单条失败不影响其它条。"
          >
            <Input placeholder="user id" />
          </Form.Item>
        </Form>
      </Modal>
    </Space>
  );
}

function parseVariables(text?: string): Variables | undefined {
  const trimmed = text?.trim();

  if (!trimmed) {
    return undefined;
  }

  const parsed = JSON.parse(trimmed) as unknown;

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new Error('变量必须是 JSON 对象');
  }

  return parsed as Variables;
}

function validateVariables(_: unknown, value?: string): Promise<void> {
  try {
    parseVariables(value);
    return Promise.resolve();
  } catch (error) {
    return Promise.reject(error instanceof Error ? error : new Error('变量必须是 JSON 对象'));
  }
}

/** ISO / UTC 时间 → `<input type="datetime-local">` 需要的本地时间字符串。 */
function toLocalDateTime(iso?: string | null): string | undefined {
  if (!iso) {
    return undefined;
  }

  const date = new Date(iso);

  if (Number.isNaN(date.getTime())) {
    return undefined;
  }

  const pad = (value: number) => String(value).padStart(2, '0');

  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** 本地时间字符串 → ISO（带时区）；空 / 非法返回 undefined。 */
function toIsoOrUndefined(local?: string): string | undefined {
  if (!local) {
    return undefined;
  }

  const date = new Date(local);

  return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}

/** 去掉筛选值的首尾空白，空串直接丢弃（查询串与缓存键都不该带空条件）。 */
function normalizeFilters<T extends object>(filter: T): T {
  const result: Record<string, string> = {};

  for (const [key, value] of Object.entries(filter)) {
    if (typeof value !== 'string') {
      continue;
    }

    const trimmed = value.trim();

    if (trimmed.length > 0) {
      result[key] = trimmed;
    }
  }

  return result as T;
}

/** 两组筛选条件在值上是否等价（undefined 与空串视为相同）。 */
function isSameFilter(left: object, right: object): boolean {
  const a = left as Record<string, unknown>;
  const b = right as Record<string, unknown>;
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);

  return [...keys].every((key) => (a[key] ?? '') === (b[key] ?? ''));
}

/** 筛选条件是否非空（字符串字段去空白后判断，空串视同未填）。 */
function hasTextFilter(filter: object): boolean {
  return Object.values(filter).some((value) => typeof value === 'string' && value.trim().length > 0);
}

/**
 * 三个列表共用的筛选行：左侧放各列表自己的输入项，右侧统一「查询 / 重置」。
 * 条件只在点查询（或回车）时生效，避免每敲一个字都发一次请求。
 */
function FilterBar({
  children,
  active,
  onSearch,
  onReset,
}: {
  children: ReactNode;
  /** 是否已有生效中的条件（控制「重置」可用态与提示）。 */
  active: boolean;
  onSearch: () => void;
  onReset: () => void;
}) {
  return (
    <Space wrap>
      {children}
      <Button type="primary" onClick={onSearch}>
        查询
      </Button>
      <Button onClick={onReset} disabled={!active}>
        重置
      </Button>
      {active && <Typography.Text type="secondary">已按条件筛选</Typography.Text>}
    </Space>
  );
}
