import type { DeploymentSummaryResponse } from '../api/types';

/**
 * 把部署列表分成「当前」与「历史」两类。
 *
 * <para>
 * 发布一次 = 引擎新建一次<b>部署</b>（`ACT_RE_DEPLOYMENT`），所以同一流程每发一版，
 * 部署管理里就多一行。判断依据是版本号：某次部署里的<b>每个</b>流程定义都能被「更早出现的
 * 同一版本」或「更高版本」取代时，它才整体算历史；只要里面还有一个是当前版本，这次部署
 * 就留在当前列表（一次部署可以含多个定义，不能因为其中一个过时就整行藏掉）。
 * </para>
 * <para>
 * 判「更早出现」而不是只判版本号大小：同一个版本可能被重复部署（另存为模型再发布、
 * 关掉幂等重复导入），两份内容一样、只差部署记录，列表里同样不该并排两行。
 * 入参按部署时间倒序（接口就是这么返回的），所以先到的那次留下、后到的归历史。
 * </para>
 * <para>
 * 只影响展示：部署实体、资源清单、删除部署仍然按部署走 —— 旧版本不是被"合并删除"，
 * 而是收进开关后面，随时可看，也可以在行内「查看历史版本」逐版查看。
 * </para>
 */
export function splitDeploymentsByVersion(deployments: DeploymentSummaryResponse[]): {
  activeDeployments: DeploymentSummaryResponse[];
  historyDeployments: DeploymentSummaryResponse[];
} {
  const latestVersionByKey = new Map<string, number>();

  for (const deployment of deployments) {
    for (const definition of deployment.definitions) {
      const current = latestVersionByKey.get(definition.key) ?? 0;

      if (definition.version > current) {
        latestVersionByKey.set(definition.key, definition.version);
      }
    }
  }

  const activeDeployments: DeploymentSummaryResponse[] = [];
  const historyDeployments: DeploymentSummaryResponse[] = [];

  // 已出现过的最新版本（`key:version`）：同一版本被重复部署时，只让先到的那次留在当前列表。
  const claimed = new Set<string>();

  for (const deployment of deployments) {
    const carriesCurrent =
      deployment.definitions.length === 0
      || deployment.definitions.some((definition) => {
        const latest = latestVersionByKey.get(definition.key) ?? definition.version;

        return definition.version === latest && !claimed.has(`${definition.key}:${definition.version}`);
      });

    (carriesCurrent ? activeDeployments : historyDeployments).push(deployment);

    for (const definition of deployment.definitions) {
      if (definition.version === (latestVersionByKey.get(definition.key) ?? definition.version)) {
        claimed.add(`${definition.key}:${definition.version}`);
      }
    }
  }

  return { activeDeployments, historyDeployments };
}
