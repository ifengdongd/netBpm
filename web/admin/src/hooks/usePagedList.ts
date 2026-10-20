import { useCallback, useEffect, useRef, useState } from 'react';

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

/**
 * 分页列表 Hook：统一封装 items / total / page / loading 状态与加载错误提示。
 * <para>
 * 内部带请求序号防竞态：翻页很快时，旧页的后到响应会被丢弃，避免表格内容与页码错位。
 * </para>
 *
 * @param fetcher 按页拉取数据的 API 调用（返回 `{ items, total }`）。
 * @param onError 加载失败时的提示回调（一般是 message.error）。
 * @param defaultPageSize 初始页大小。
 */
export function usePagedList<T>(
  fetcher: (page: PageState) => Promise<PagedResponse<T>>,
  onError: (error: Error) => void,
  defaultPageSize = 20,
): PagedList<T> {
  const [items, setItems] = useState<T[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState<PageState>({ current: 1, pageSize: defaultPageSize });
  const [loading, setLoading] = useState(false);

  // 通过 ref 让 load / reload 始终拿到最新的 fetcher 与 onError，而不重挂 useEffect
  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;
  const onErrorRef = useRef(onError);
  onErrorRef.current = onError;

  const pageRef = useRef(page);
  pageRef.current = page;

  // 请求序号：只有最新一次请求允许写状态
  const seqRef = useRef(0);

  const load = useCallback(async (target: PageState) => {
    const seq = ++seqRef.current;
    setLoading(true);

    try {
      const result = await fetcherRef.current(target);

      if (seq !== seqRef.current) {
        return; // 过期响应，丢弃
      }

      setItems(result.items);
      setTotal(result.total);
    } catch (error) {
      if (seq === seqRef.current) {
        onErrorRef.current(error as Error);
      }
    } finally {
      if (seq === seqRef.current) {
        setLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    void load(page);
  }, [page, load]);

  const reload = useCallback(() => {
    void load(pageRef.current);
  }, [load]);

  const pagination: PagedListPagination = {
    current: page.current,
    pageSize: page.pageSize,
    total,
    showSizeChanger: true,
    showTotal: (value) => `共 ${value} 条`,
    onChange: (current, pageSize) => setPage({ current, pageSize }),
  };

  return { items, total, page, loading, setPage, reload, pagination };
}
