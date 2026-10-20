import { Navigate, Route, Routes } from 'react-router-dom';

import AppLayout from './components/AppLayout';
import DeploymentsPage from './pages/DeploymentsPage';
import LoginPage from './pages/LoginPage';
import ModelEditorPage from './pages/ModelEditorPage';
import ModelsPage from './pages/ModelsPage';
import MonitoringPage from './pages/MonitoringPage';
import ProcessTypesPage from './pages/ProcessTypesPage';

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<AppLayout />}>
        <Route index element={<Navigate to="/models" replace />} />
        <Route path="/models" element={<ModelsPage />} />
        <Route path="/models/editor" element={<ModelEditorPage />} />
        <Route path="/process-types" element={<ProcessTypesPage />} />
        <Route path="/deployments" element={<DeploymentsPage />} />
        <Route path="/monitoring" element={<MonitoringPage />} />
        <Route path="*" element={<Navigate to="/models" replace />} />
      </Route>
    </Routes>
  );
}
