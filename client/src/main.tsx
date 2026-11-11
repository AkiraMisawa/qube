import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import '@mantine/core/styles.css';
import '@ex-grid/core/styles.css';
import '@ex-grid/mantine/styles.css';
import './theme.css';
import './shell/shell.css';
import { App } from './App';
import { httpApi } from './platform/api';
import { startTelemetry, telemetry } from './platform/telemetry';
import { themeManager } from './platform/settings/theme';
import { cssVariablesResolver, theme } from './theme';

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } } });

// The workbench opens on a valid context so the first command can run at once; the user rebinds it.
const today = new Date().toISOString().slice(0, 10);

startTelemetry();

// The theme is the user's setting (ADR-0017): one manager for the page's life, which follows it as it is read and changed.
const colorSchemes = themeManager();

const root = document.getElementById('root');
if (root === null) throw new Error('index.html has no #root element.');
createRoot(root, {
  // A render error that no boundary caught takes the page down: the server's log gets it with the component stack.
  onUncaughtError: (error, info) => {
    console.error(error);
    const stack = [error instanceof Error ? error.stack : undefined, info.componentStack].filter((s) => s !== undefined).join('\n');
    telemetry.report({ level: 'error', kind: 'render', message: error instanceof Error ? error.message : String(error), ...(stack === '' ? {} : { stack }) });
  },
}).render(
  <StrictMode>
    <MantineProvider theme={theme} cssVariablesResolver={cssVariablesResolver} defaultColorScheme="dark" colorSchemeManager={colorSchemes}>
      <QueryClientProvider client={queryClient}>
        <App api={httpApi} initialContext={{ region: 'North', asOf: today }} />
      </QueryClientProvider>
    </MantineProvider>
  </StrictMode>,
);
