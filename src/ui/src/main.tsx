import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';
import { applyTheme, watchSystemTheme } from './prefs';

// Тема — до первой отрисовки, чтобы окно не мигало тёмным.
applyTheme();
watchSystemTheme(() => location.reload());

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>);
