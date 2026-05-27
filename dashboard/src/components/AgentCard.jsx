import { formatDuration, formatRelativeTime } from '../utils/formatting';

function getStatusStyles(status) {
  switch (status) {
    case 'completed':
      return {
        badge: 'bg-green-100 text-green-700',
        border: 'border-green-200',
      };
    case 'active':
      return {
        badge: 'bg-sky-100 text-sky-700',
        border: 'border-sky-400 shadow-[0_0_0_1px_rgba(103,191,255,0.5)] animate-pulse',
      };
    case 'failed':
      return {
        badge: 'bg-red-100 text-red-700',
        border: 'border-red-200',
      };
    default:
      return {
        badge: 'bg-gray-100 text-gray-600',
        border: 'border-gray-200',
      };
  }
}

/**
 * @param {{agent: {name: string, status: string, lastRun?: string, storiesProcessed?: number, avgDurationSeconds?: number, detail?: string}}} props
 */
export default function AgentCard({ agent }) {
  const safeAgent = agent ?? {};
  const styles = getStatusStyles(safeAgent.status);
  const agentName = String(safeAgent.name ?? 'UnknownAgent');

  return (
    <div className={`min-w-[220px] rounded-xl border bg-white p-4 shadow-xs ${styles.border}`}>
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className="text-sm font-semibold text-gray-900">{agentName.replace('Agent', ' Agent')}</div>
          <div className="mt-1 text-xs text-gray-400">Last run {formatRelativeTime(safeAgent.lastRun)}</div>
          {safeAgent.detail ? <div className="mt-1 truncate text-xs font-medium text-gray-600">{safeAgent.detail}</div> : null}
        </div>
        <span className={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold capitalize ${styles.badge}`}>
          {safeAgent.status ?? 'idle'}
        </span>
      </div>
      <dl className="mt-5 grid grid-cols-2 gap-3 text-sm">
        <div className="rounded-lg bg-gray-50 p-3">
          <dt className="text-xs uppercase tracking-[0.12em] text-gray-400">Processed</dt>
          <dd className="mt-2 text-lg font-semibold text-gray-900">{safeAgent.storiesProcessed ?? 0}</dd>
        </div>
        <div className="rounded-lg bg-gray-50 p-3">
          <dt className="text-xs uppercase tracking-[0.12em] text-gray-400">Avg Duration</dt>
          <dd className="mt-2 text-lg font-semibold text-gray-900">{formatDuration(safeAgent.avgDurationSeconds)}</dd>
        </div>
      </dl>
    </div>
  );
}
