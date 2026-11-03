import { describe, expect, it } from 'vitest';

import type { DeploymentDefinitionResponse, DeploymentSummaryResponse } from '../api/types';
import { splitDeploymentsByVersion } from './deployments';

/** 构造一条部署；只覆盖断言用到的字段，其余给安全默认值。 */
function deployment(
  deploymentId: string,
  definitions: Array<Partial<DeploymentDefinitionResponse> & { key: string; version: number }>,
): DeploymentSummaryResponse {
  const normalized: DeploymentDefinitionResponse[] = definitions.map((definition) => ({
    processDefinitionId: `${definition.key}:${definition.version}:${deploymentId}`,
    key: definition.key,
    name: definition.name ?? '请假流程',
    version: definition.version,
    resourceName: `${definition.key}.bpmn20.xml`,
    isSuspended: false,
  }));

  return {
    deploymentId,
    name: '模型发布：请假流程',
    deployTime: '2026-11-03T07:15:00.893Z',
    tenantId: '',
    definitions: normalized,
    definitionCount: normalized.length,
    latestVersion: Math.max(0, ...normalized.map((d) => d.version)),
    suspendedCount: 0,
  };
}

describe('splitDeploymentsByVersion', () => {
  it('发布新版本后，只留最新那次部署，旧部署归入历史', () => {
    const { activeDeployments, historyDeployments } = splitDeploymentsByVersion([
      deployment('15152', [{ key: 'leave_process', version: 4 }]),
      deployment('10002', [{ key: 'leave_process', version: 3 }]),
    ]);

    expect(activeDeployments.map((d) => d.deploymentId)).toEqual(['15152']);
    expect(historyDeployments.map((d) => d.deploymentId)).toEqual(['10002']);
  });

  it('不同流程各自保留最新一次，互不影响', () => {
    const { activeDeployments, historyDeployments } = splitDeploymentsByVersion([
      deployment('15152', [{ key: 'leave_process', version: 4 }]),
      deployment('10002', [{ key: 'leave_process', version: 3 }]),
      deployment('10005', [{ key: 'order_process', version: 3 }]),
    ]);

    expect(activeDeployments.map((d) => d.deploymentId)).toEqual(['15152', '10005']);
    expect(historyDeployments.map((d) => d.deploymentId)).toEqual(['10002']);
  });

  it('一次部署含多个定义时，只要有一个是最新版就留在当前列表', () => {
    const { activeDeployments, historyDeployments } = splitDeploymentsByVersion([
      deployment('20000', [
        { key: 'leave_process', version: 5 },
        { key: 'order_process', version: 3 },
      ]),
      deployment('10005', [{ key: 'order_process', version: 3 }]),
    ]);

    // order_process v3 在两处都出现，20000 里还有 leave v5 → 20000 是当前；10005 已被 20000 覆盖
    expect(activeDeployments.map((d) => d.deploymentId)).toEqual(['20000']);
    expect(historyDeployments.map((d) => d.deploymentId)).toEqual(['10005']);
  });

  it('没有定义的空部署不算历史，避免整行凭空消失', () => {
    const { activeDeployments, historyDeployments } = splitDeploymentsByVersion([deployment('1', [])]);

    expect(activeDeployments.map((d) => d.deploymentId)).toEqual(['1']);
    expect(historyDeployments).toEqual([]);
  });

  it('只有一次部署时不折叠', () => {
    const { activeDeployments, historyDeployments } = splitDeploymentsByVersion([
      deployment('10005', [{ key: 'order_process', version: 3 }]),
    ]);

    expect(activeDeployments).toHaveLength(1);
    expect(historyDeployments).toEqual([]);
  });
});
