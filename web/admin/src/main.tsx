import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App as AntdApp, ConfigProvider } from 'antd';
import zhCN from 'antd/locale/zh_CN';
import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';

import App from './App';
import { APP_BASE } from './api/base';
import AppErrorBoundary from './components/AppErrorBoundary';

import 'antd/dist/reset.css';

// 全局查询客户端：
//   - 4xx 不重试（401 已跳登录、403 重复请求无意义），其余失败重试 1 次；
//   - 短暂 staleTime 内命中缓存，操作后由 invalidateQueries 主动刷新。
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: (failureCount, error) => {
        const status = (error as { status?: number }).status;
        return !(typeof status === 'number' && status >= 400 && status < 500) && failureCount < 1;
      },
      refetchOnWindowFocus: false,
      staleTime: 5_000,
    },
  },
});

ReactDOM.createRoot(document.getElementById('root') as HTMLElement).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <ConfigProvider locale={zhCN}>
        <AntdApp>
          {/* 在 ConfigProvider / AntdApp 之内：兜底页用到 antd 组件与主题 */}
          <AppErrorBoundary>
            {/* basename = 部署前缀：挂在 /netbpm/ 下时路由与链接都自动带上前缀 */}
            <BrowserRouter basename={APP_BASE === '' ? undefined : APP_BASE}>
              <App />
            </BrowserRouter>
          </AppErrorBoundary>
        </AntdApp>
      </ConfigProvider>
    </QueryClientProvider>
  </React.StrictMode>,
);
