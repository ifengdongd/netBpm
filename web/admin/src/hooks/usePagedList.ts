import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';

import type { PagedResponse } from '../api/types';

export interface PageState {
  current: number;
  pageSize: number;
}

/** antd Table 分页配置的最小形状（本项目所有分页表格共用）。 */
export interface PagedListPagination {
  current: number;
  pageSize: number;
  total: number;
  showSizeChanger: true;
  showTotal: (total: number) => string;
  onChange: (current: number, pageSize: number) => void;
}

export interface PagedList<T> {
  items: T[];
  total: number;
  page: PageState;
  loading: boolean;
  /** 翻页 / 改页大小。 */
  setPage: (page: PageState) => void;
  /** 按当前页码重新拉取（刷新按钮 / 操作后同步）。 */
  reload: () => void;
  /** 直接给 antd Table 的 pagination 属性用。 */
  pagination: PagedListPagination;
}

/** usePagedList 可选项。 */
export interface PagedListOptions {
  /** 初始页大小（默认 20）。 */
  defaultPageSize?: number;
  /** 轮询间隔毫秒数（自动刷新）；缺省 false 不轮询。 */
  refetchInterval?: number | false;
}

/**
 * 分页列表 Hook：基于 TanStack Query 封装 items / total / page / loading 状态与加载错误提示。
 * <para>
 * - 由 queryKey 自动获得缓存、去重与跨组件失效（操作后 `invalidateQueries` 即可联动刷新）；
 * - 翻页时保留上一页数据（`keepPreviousData`），表格不闪烁，且天然无过期响应竞态。
 * </para>
 *
 * @param queryKey 查询缓存键前缀（如 `['runtime', 'instances']`，分页状态会自动追加）。
 * @param fetcher 按页拉取数据的 API 调用（返回 `{ items, total }`）。
 * @param onError 加载失败时的提示回调（一般是 message.error）。
 * @param defaultPageSize 初始页大小。
 */
export function usePagedList<T>(
  queryKey: readonly unknown[],
  fetcher: (page: PageState) => Promise<PagedResponse<T>>,
  onError: (error: Error) => void,
  options?: PagedListOptions,
): PagedList<T> {
  const [page, setPage] = useState<PageState>({ current: 1, pageSize: options?.defaultPageSize ?? 20 });

  // 通过 ref 让错误提示始终拿到最新回调，而不重挂 useEffect
  const onErrorRef = useRef(onError);

  useEffect(() => {
    onErrorRef.current = onError;
  });

  const query = useQuery({
    queryKey: [...queryKey, page],
    queryFn: () => fetcher(page),
    // 翻页时保留上一页数据，避免表格清空闪烁
    placeholderData: keepPreviousData,
    refetchInterval: options?.refetchInterval ?? false,
  });

  // 查询失败时提示一次
  useEffect(() => {
    if (query.isError) {
      onErrorRef.current(query.error as Error);
    }
  }, [query.isError, query.error]);

  return {
    items: query.data?.items ?? [],
    total: query.data?.total ?? 0,
    page,
    loading: query.isPending || query.isFetching,
    setPage,
    reload: () => void query.refetch(),
    pagination: {
      current: page.current,
      pageSize: page.pageSize,
      total: query.data?.total ?? 0,
      showSizeChanger: true,
      showTotal: (value) => `共 ${value} 条`,
      onChange: (current, pageSize) => setPage({ current, pageSize }),
    },
  };
}
