import { formatDistanceToNowStrict } from 'date-fns';

import { STATUS_LABELS } from '../constants';
import BrandLogo from './BrandLogo';

function statusClasses(status) {
  switch (status) {
    case 'live':
      return 'bg-green-500 text-green-700';
    case 'reconnecting':
      return 'bg-yellow-400 text-yellow-700';
    case 'disconnected':
      return 'bg-red-500 text-red-700';
    case 'paused':
      return 'bg-gray-400 text-gray-600';
    default:
      return 'bg-sky-500 text-sky-700';
  }
}

export default function AppHeader({
  connectionStatus,
  lastUpdated,
  onLogout,
  pageTitle,
  sidebarOpen,
  setSidebarOpen,
}) {
  const lastUpdatedLabel = lastUpdated
    ? `${formatDistanceToNowStrict(new Date(lastUpdated), { addSuffix: true })}`
    : 'Awaiting first sync';

  return (
    <header className="sticky top-0 z-30 before:absolute before:inset-0 before:-z-10 before:bg-gray-100/90 before:backdrop-blur-md max-lg:shadow-xs">
      <div className="px-4 sm:px-6 lg:px-8">
        <div className="flex h-16 items-center justify-between border-b border-gray-200">
          <div className="flex items-center gap-4">
            <button
              className="text-gray-500 hover:text-gray-600 lg:hidden"
              aria-controls="sidebar"
              aria-expanded={sidebarOpen}
              onClick={(event) => {
                event.stopPropagation();
                setSidebarOpen(!sidebarOpen);
              }}
            >
              <span className="sr-only">Open sidebar</span>
              <svg className="h-6 w-6 fill-current" viewBox="0 0 24 24">
                <rect x="4" y="5" width="16" height="2" />
                <rect x="4" y="11" width="16" height="2" />
                <rect x="4" y="17" width="16" height="2" />
              </svg>
            </button>
            <BrandLogo className="hidden h-9 w-9 sm:block" />
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.18em] text-gray-400">ADOm8</div>
              <h1 className="text-lg font-semibold text-gray-900">{pageTitle}</h1>
            </div>
          </div>

          <div className="flex items-center gap-3">
            <div className="flex items-center gap-2 rounded-full border border-gray-200 bg-white px-3 py-1.5 text-sm text-gray-600">
              <span className={`inline-flex h-2.5 w-2.5 rounded-full ${statusClasses(connectionStatus)} ${connectionStatus === 'live' ? 'animate-pulse' : ''}`} />
              <span className="font-medium">{STATUS_LABELS[connectionStatus] ?? STATUS_LABELS.connecting}</span>
              <span className="hidden text-gray-400 sm:inline">|</span>
              <span className="hidden sm:inline">{lastUpdatedLabel}</span>
            </div>
            <button
              onClick={onLogout}
              className="btn border-gray-200 bg-white text-gray-700 hover:border-gray-300 hover:text-gray-900"
            >
              Log out
            </button>
          </div>
        </div>
      </div>
    </header>
  );
}
