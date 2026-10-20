import { Spin } from 'antd';
import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';

import AppLayout from './components/AppLayout';

// 路由级代码分割：每个页面独立 chunk，首屏只加载当前页面所需代码
// （bpmn-js 体积大，在 BpmnDiagram 组件内已单独按需加载）
const AuditPage = lazy(() => import('./pages/AuditPage'));
const DeploymentsPage = lazy(() => import('./pages/DeploymentsPage'));
const LoginPage = lazy(() => import('./pages/LoginPage'));
const MetricsPage = lazy(() => import('./pages/MetricsPage'));
const ModelEditorPage = lazy(() => import('./pages/ModelEditorPage'));
const ModelsPage = lazy(() => import('./pages/ModelsPage'));
const MonitoringPage = lazy(() => import('./pages/MonitoringPage'));
const ProcessTypesPage = lazy(() => import('./pages/ProcessTypesPage'));

/** 路由切换时的加载占位。 */
function PageFallback() {
  return <Spin size="large" style={{ display: 'block', margin: '120px auto' }} />;
}

export default function App() {
  return (
    <Suspense fallback={<PageFallback />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route element={<AppLayout />}>
          <Route index element={<Navigate to="/models" replace />} />
          <Route path="/models" element={<ModelsPage />} />
          <Route path="/models/editor" element={<ModelEditorPage />} />
          <Route path="/process-types" element={<ProcessTypesPage />} />
          <Route path="/deployments" element={<DeploymentsPage />} />
          <Route path="/monitoring" element={<MonitoringPage />} />
          <Route path="/audit" element={<AuditPage />} />
          <Route path="/metrics" element={<MetricsPage />} />
          <Route path="*" element={<Navigate to="/models" replace />} />
        </Route>
      </Routes>
    </Suspense>
  );
}
