import { Button, Result } from 'antd';
import { Component } from 'react';
import type { ErrorInfo, ReactNode } from 'react';

interface State {
  error: Error | null;
}

/**
 * 全局错误兜底：任一页面 / 抽屉组件抛出未捕获异常时展示可恢复的错误页，
 * 而不是白屏。典型场景：发布新版本后旧页面按需加载已失效的 chunk（懒加载 404）。
 * 「刷新页面」整页重载后即恢复；antd 组件需在 ConfigProvider 之下，挂载点见 main.tsx。
 */
export default class AppErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[AppErrorBoundary]', error, info.componentStack);
  }

  render() {
    if (this.state.error !== null) {
      return (
        <Result
          status="error"
          title="页面出错了"
          subTitle={this.state.error.message}
          extra={
            <Button type="primary" onClick={() => window.location.reload()}>
              刷新页面
            </Button>
          }
        />
      );
    }

    return this.props.children;
  }
}
