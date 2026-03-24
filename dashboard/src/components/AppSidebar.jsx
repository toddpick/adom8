import { useMemo, useState } from 'react';
import { NavLink } from 'react-router-dom';

import packageJson from '../../package.json';
import { initializeCodebase } from '../api';
import BrandLogo from './BrandLogo';
import { formatRelativeTime } from '../utils/formatting';

function NavItem({ to, label, icon, end = false }) {
  return (
    <li>
      <NavLink
        end={end}
        to={to}
        className={({ isActive }) => `flex items-center rounded-lg px-3 py-2 text-sm font-medium transition ${
          isActive ? 'bg-violet-500/[0.12] text-violet-600' : 'text-gray-700 hover:bg-gray-100 hover:text-gray-900'
        }`}
      >
        {({ isActive }) => (
          <>
            <span className={`mr-3 transition ${isActive ? 'text-violet-500' : 'text-gray-400'}`}>{icon}</span>
            <span>{label}</span>
          </>
        )}
      </NavLink>
    </li>
  );
}

function getHealthTone(status) {
  switch (status) {
    case 'healthy':
      return {
        dot: 'bg-emerald-500',
        text: 'text-emerald-700',
        border: 'border-emerald-200 bg-emerald-50',
      };
    case 'degraded':
      return {
        dot: 'bg-amber-500',
        text: 'text-amber-700',
        border: 'border-amber-200 bg-amber-50',
      };
    case 'unhealthy':
      return {
        dot: 'bg-red-500',
        text: 'text-red-700',
        border: 'border-red-200 bg-red-50',
      };
    default:
      return {
        dot: 'bg-gray-400',
        text: 'text-gray-500',
        border: 'border-gray-200 bg-gray-50',
      };
  }
}

function getToneStatus(status) {
  const normalized = String(status ?? '').toLowerCase();

  if (['healthy', 'configured', 'enabled', 'active', 'success'].includes(normalized)) {
    return 'healthy';
  }

  if (['degraded', 'warning', 'pending'].includes(normalized)) {
    return 'degraded';
  }

  if (['unhealthy', 'failed', 'error'].includes(normalized)) {
    return 'unhealthy';
  }

  return 'unknown';
}

function formatStatusLabel(status) {
  return String(status ?? 'unknown')
    .replaceAll('_', ' ')
    .trim();
}

function HealthRow({ label, check }) {
  const tone = getHealthTone(getToneStatus(check?.status));
  const queueMeta = label === 'Queue' && check
    ? `${check.messageCount ?? 0} queued${check.poisonMessageCount ? ` • ${check.poisonMessageCount} poison` : ''}`
    : null;

  return (
    <div className="flex items-center justify-between gap-3 rounded-lg px-3 py-2 hover:bg-gray-50">
      <div className="flex min-w-0 items-center gap-3">
        <span className={`inline-flex h-2.5 w-2.5 rounded-full ${tone.dot}`} />
        <div className="min-w-0">
          <div className="text-sm font-medium text-gray-800">{label}</div>
          <div className="truncate text-xs text-gray-400">{queueMeta ?? check?.message ?? 'Waiting for signal'}</div>
        </div>
      </div>
      <span className={`shrink-0 text-xs font-semibold uppercase ${tone.text}`}>
        {formatStatusLabel(check?.status ?? 'unknown')}
      </span>
    </div>
  );
}

function ProviderPill({ name, model, status, detail }) {
  const tone = getHealthTone(getToneStatus(status));

  return (
    <div className={`rounded-xl border px-3 py-2 ${tone.border}`}>
      <div className="flex items-center justify-between gap-3">
        <div className="min-w-0">
          <div className="truncate text-sm font-semibold text-gray-900">{name}</div>
          <div className="truncate text-xs text-gray-500">{model || detail || 'No model configured'}</div>
          {status ? <div className={`mt-1 text-[11px] font-semibold uppercase ${tone.text}`}>{formatStatusLabel(status)}</div> : null}
        </div>
        <span className={`inline-flex h-2.5 w-2.5 rounded-full ${tone.dot}`} />
      </div>
    </div>
  );
}

function buildProviderCards(healthData) {
  const providers = healthData?.providers;
  const cards = [];

  if (providers?.ai) {
    cards.push({
      name: providers.ai.name,
      model: providers.ai.model,
      status: providers.ai.status ?? (providers.ai.configured ? 'configured' : 'unknown'),
      detail: providers.ai.configured ? 'Configured' : 'Not configured',
    });
  }

  if (providers?.copilot) {
    cards.push({
      name: 'GitHub Copilot',
      model: providers.copilot.model ?? providers.copilot.mode,
      status: providers.copilot.status ?? (providers.copilot.enabled ? 'enabled' : 'disabled'),
      detail: providers.copilot.enabled ? 'Enabled' : 'Disabled',
    });
  }

  for (const provider of providers?.additionalProviders ?? []) {
    cards.push({
      name: provider.name,
      model: provider.model,
      status: provider.status ?? (provider.configured ? 'configured' : 'not_configured'),
      detail: provider.configured ? 'Configured' : 'Not configured',
    });
  }

  return cards;
}

function CodebaseCard({
  appKey,
  codebaseData,
  codebaseError,
  codebaseLoading,
  onUnauthorized,
  refreshCodebase,
  refreshHealth,
  refreshStatus,
}) {
  const [initializing, setInitializing] = useState(false);
  const [feedback, setFeedback] = useState(null);

  const hasAnalysis = Boolean(codebaseData?.lastAnalysis);
  const stats = codebaseData?.stats;

  const handleInitializeCodebase = async () => {
    if (!appKey || initializing) {
      return;
    }

    setInitializing(true);
    setFeedback(null);

    try {
      const response = await initializeCodebase(appKey);
      await Promise.allSettled([
        refreshCodebase?.(),
        refreshHealth?.(),
        refreshStatus?.(),
      ]);

      setFeedback({
        type: 'success',
        message: response?.nextStep ?? 'Codebase initialization started successfully.',
        workItemUrl: response?.workItemUrl ?? null,
        workItemId: response?.workItemId ?? null,
      });
    } catch (error) {
      if (error.code === 401 || error.code === 403) {
        onUnauthorized?.();
        return;
      }

      setFeedback({
        type: 'error',
        message: error.message || 'Failed to initialize codebase.',
      });
    } finally {
      setInitializing(false);
    }
  };

  return (
    <div>
      <h3 className="pl-3 text-xs font-semibold uppercase text-gray-400">Codebase Intelligence</h3>
      <div className="mt-3 rounded-xl border border-gray-200 bg-gray-50 p-4">
        <div className="flex items-start justify-between gap-3">
          <div>
            <div className="text-sm font-semibold text-gray-900">
              {hasAnalysis ? 'Codebase Initialized' : 'Codebase Not Analyzed'}
            </div>
            <div className="mt-1 text-xs text-gray-500">
              {codebaseLoading
                ? 'Loading codebase status...'
                : hasAnalysis
                  ? `Last scan ${formatRelativeTime(codebaseData.lastAnalysis)}`
                  : 'No scan has been recorded yet.'}
            </div>
          </div>
          <span className={`inline-flex h-2.5 w-2.5 rounded-full ${hasAnalysis ? 'bg-emerald-500' : 'bg-gray-400'}`} />
        </div>

        {stats ? (
          <div className="mt-4 grid grid-cols-2 gap-2 text-xs text-gray-600">
            <div className="rounded-lg bg-white px-3 py-2">Files: <span className="font-semibold text-gray-900">{stats.filesAnalyzed ?? 0}</span></div>
            <div className="rounded-lg bg-white px-3 py-2">LOC: <span className="font-semibold text-gray-900">{stats.linesOfCode ?? 0}</span></div>
            <div className="rounded-lg bg-white px-3 py-2">Framework: <span className="font-semibold text-gray-900">{stats.primaryFramework ?? 'Unknown'}</span></div>
            <div className="rounded-lg bg-white px-3 py-2">Languages: <span className="font-semibold text-gray-900">{stats.languagesDetected?.length ?? 0}</span></div>
          </div>
        ) : null}

        <button
          onClick={handleInitializeCodebase}
          disabled={initializing}
          className="mt-4 inline-flex w-full items-center justify-center rounded-xl bg-violet-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-violet-700 disabled:cursor-not-allowed disabled:bg-violet-300"
        >
          {initializing ? 'Starting...' : hasAnalysis ? 'Re-run Codebase Init' : 'Initialize Codebase'}
        </button>

        {codebaseError ? <div className="mt-3 text-xs text-red-600">{codebaseError}</div> : null}
        {feedback ? (
          <div className={`mt-3 rounded-lg px-3 py-2 text-xs ${feedback.type === 'success' ? 'bg-emerald-50 text-emerald-700' : 'bg-red-50 text-red-700'}`}>
            <div>{feedback.message}</div>
            {feedback.workItemUrl ? (
              <a
                href={feedback.workItemUrl}
                target="_blank"
                rel="noreferrer"
                className="mt-2 inline-flex font-semibold underline underline-offset-2"
              >
                Open work item #{feedback.workItemId}
              </a>
            ) : null}
          </div>
        ) : null}
      </div>
    </div>
  );
}

export default function AppSidebar({
  appKey,
  codebaseData,
  codebaseError,
  codebaseLoading,
  healthData,
  healthError,
  healthLoading,
  onUnauthorized,
  refreshCodebase,
  refreshHealth,
  refreshStatus,
  sidebarOpen,
  setSidebarOpen,
}) {
  const healthChecks = healthData?.checks ?? {};
  const providerCards = useMemo(() => buildProviderCards(healthData), [healthData]);

  return (
    <div className="min-w-fit">
      <div
        className={`fixed inset-0 z-40 bg-gray-900/30 transition-opacity duration-200 lg:hidden ${
          sidebarOpen ? 'opacity-100' : 'pointer-events-none opacity-0'
        }`}
        aria-hidden="true"
        onClick={() => setSidebarOpen(false)}
      />

      <aside
        id="sidebar"
        className={`absolute left-0 top-0 z-40 flex h-[100dvh] w-72 shrink-0 -translate-x-72 flex-col overflow-y-auto rounded-r-2xl bg-white p-4 shadow-xs transition-all duration-200 ease-in-out lg:static lg:translate-x-0 ${
          sidebarOpen ? 'translate-x-0' : ''
        }`}
      >
        <div className="mb-8 flex items-center justify-between pr-3 sm:px-2">
          <button
            className="text-gray-500 hover:text-gray-400 lg:hidden"
            onClick={() => setSidebarOpen(false)}
            aria-controls="sidebar"
            aria-expanded={sidebarOpen}
          >
            <span className="sr-only">Close sidebar</span>
            <svg className="h-6 w-6 fill-current" viewBox="0 0 24 24">
              <path d="M10.7 18.7l1.4-1.4L7.8 13H20v-2H7.8l4.3-4.3-1.4-1.4L4 12z" />
            </svg>
          </button>
          <NavLink end to="/" className="flex items-center gap-3">
            <BrandLogo className="h-11 w-11" />
            <span>
              <span className="block text-sm font-semibold text-gray-900">ADOm8</span>
              <span className="block text-xs uppercase tracking-[0.2em] text-gray-400">Dashboard</span>
            </span>
          </NavLink>
        </div>

        <div className="space-y-8">
          <div>
            <h3 className="pl-3 text-xs font-semibold uppercase text-gray-400">Navigation</h3>
            <ul className="mt-3 space-y-1">
              <NavItem
                to="/"
                end
                label="Overview"
                icon={(
                  <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
                    <path d="M5.936.278A7.983 7.983 0 0 1 8 0a8 8 0 1 1-8 8c0-.722.104-1.413.278-2.064a1 1 0 1 1 1.932.516A5.99 5.99 0 0 0 2 8a6 6 0 1 0 6-6c-.53 0-1.045.076-1.548.21A1 1 0 1 1 5.936.278Z" />
                    <path d="M6.068 7.482A2.003 2.003 0 0 0 8 10a2 2 0 1 0-.518-3.932L3.707 2.293a1 1 0 0 0-1.414 1.414l3.775 3.775Z" />
                  </svg>
                )}
              />
              <NavItem
                to="/log"
                label="Agent Log"
                icon={(
                  <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
                    <path d="M2 2h12v2H2zM2 7h12v2H2zM2 12h12v2H2z" />
                  </svg>
                )}
              />
              <NavItem
                to="/stories"
                label="Workstreams"
                icon={(
                  <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
                    <path d="M2 3.5A1.5 1.5 0 0 1 3.5 2h2A1.5 1.5 0 0 1 7 3.5v2A1.5 1.5 0 0 1 5.5 7h-2A1.5 1.5 0 0 1 2 5.5v-2ZM9 3.5A1.5 1.5 0 0 1 10.5 2h2A1.5 1.5 0 0 1 14 3.5v2A1.5 1.5 0 0 1 12.5 7h-2A1.5 1.5 0 0 1 9 5.5v-2ZM2 10.5A1.5 1.5 0 0 1 3.5 9h2A1.5 1.5 0 0 1 7 10.5v2A1.5 1.5 0 0 1 5.5 14h-2A1.5 1.5 0 0 1 2 12.5v-2ZM9 10.5A1.5 1.5 0 0 1 10.5 9h2A1.5 1.5 0 0 1 14 10.5v2A1.5 1.5 0 0 1 12.5 14h-2A1.5 1.5 0 0 1 9 12.5v-2Z" />
                  </svg>
                )}
              />
            </ul>
          </div>

          <div>
            <div className="flex items-center justify-between">
              <h3 className="pl-3 text-xs font-semibold uppercase text-gray-400">System Health</h3>
              {healthLoading ? <span className="text-xs text-gray-400">Loading…</span> : null}
            </div>
            <div className="mt-3 space-y-1">
              <HealthRow label="ADO" check={healthChecks.azureDevOps} />
              <HealthRow label="Queue" check={healthChecks.storageQueue} />
              <HealthRow label="AI" check={healthChecks.aiApi} />
              <HealthRow label="Config" check={healthChecks.configuration} />
              <HealthRow label="Git" check={healthChecks.git} />
            </div>
            {healthError ? (
              <div className={`mt-2 rounded-lg px-3 py-2 text-xs ${healthData ? 'bg-amber-50 text-amber-700' : 'text-red-600'}`}>
                {healthData ? `Latest health check reported: ${healthError}` : healthError}
              </div>
            ) : null}
          </div>

          <div>
            <h3 className="pl-3 text-xs font-semibold uppercase text-gray-400">Models</h3>
            <div className="mt-3 space-y-2">
              {providerCards.length ? providerCards.map((provider) => (
                <ProviderPill
                  key={`${provider.name}-${provider.model ?? provider.detail ?? 'unknown'}`}
                  name={provider.name}
                  model={provider.model}
                  status={provider.status}
                  detail={provider.detail}
                />
              )) : (
                <div className="rounded-xl border border-dashed border-gray-200 px-3 py-4 text-sm text-gray-500">
                  No provider information available yet.
                </div>
              )}
            </div>
          </div>

          <CodebaseCard
            appKey={appKey}
            codebaseData={codebaseData}
            codebaseError={codebaseError}
            codebaseLoading={codebaseLoading}
            onUnauthorized={onUnauthorized}
            refreshCodebase={refreshCodebase}
            refreshHealth={refreshHealth}
            refreshStatus={refreshStatus}
          />
        </div>

        <div className="mt-auto rounded-xl border border-gray-200 bg-gray-50 p-4">
          <div className="text-xs font-semibold uppercase tracking-[0.18em] text-gray-400">ADOm8</div>
          <div className="mt-2 text-sm font-medium text-gray-800">Version {packageJson.version}</div>
          <a
            href="https://adom8.dev"
            target="_blank"
            rel="noreferrer"
            className="mt-3 inline-flex text-sm font-medium text-violet-500 hover:text-violet-600"
          >
            adom8.dev
          </a>
        </div>
      </aside>
    </div>
  );
}
