import { useEffect, useMemo, useRef } from 'react';
import { Link } from 'react-router-dom';

import { formatRelativeTime } from '../utils/formatting';

/**
 * @param {{entries: Array<{timestamp: string, agent: string, workItemId: number, message: string}>, onClear?: () => void, clearing?: boolean, feedback?: {type: string, message: string} | null}} props
 */
export default function AgentActivityFeed({ entries, onClear, clearing = false, feedback = null }) {
  const containerRef = useRef(null);
  const items = useMemo(
    () =>
      [...(entries ?? [])]
        .sort((left, right) => new Date(left.timestamp).getTime() - new Date(right.timestamp).getTime())
        .slice(-20),
    [entries],
  );

  useEffect(() => {
    if (!containerRef.current) {
      return;
    }

    containerRef.current.scrollTop = containerRef.current.scrollHeight;
  }, [items]);

  return (
    <div className="col-span-full xl:col-span-5 rounded-xl bg-white shadow-xs">
      <header className="border-b border-gray-100 px-5 py-4">
        <div className="flex items-center justify-between gap-3">
          <h2 className="font-semibold text-gray-800">Activity Feed</h2>
          {onClear ? (
            <button
              onClick={onClear}
              disabled={clearing}
              className="inline-flex rounded-full border border-gray-200 bg-white px-3 py-1.5 text-xs font-semibold uppercase tracking-[0.14em] text-gray-600 transition hover:border-gray-300 hover:text-gray-900 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {clearing ? 'Clearing…' : 'Clear Activity'}
            </button>
          ) : null}
        </div>
        {feedback?.message ? (
          <div className={`mt-3 rounded-lg px-3 py-2 text-xs ${feedback.type === 'success' ? 'bg-emerald-50 text-emerald-700' : 'bg-red-50 text-red-700'}`}>
            {feedback.message}
          </div>
        ) : null}
      </header>
      <div ref={containerRef} className="max-h-[420px] space-y-1 overflow-y-auto p-3">
        {items.length ? (
          items.map((entry, index) => {
            const agentName = String(entry?.agent ?? entry?.Agent ?? 'System');
            const message = entry?.message ?? entry?.Message ?? 'Activity update';
            const timestamp = entry?.timestamp ?? entry?.TimestampUtc ?? entry?.Timestamp;
            const workItemId = entry?.workItemId ?? entry?.WorkItemId;

            return (
            <div key={`${timestamp ?? 'activity'}-${agentName}-${index}`} className="flex rounded-lg px-2 py-3 hover:bg-gray-50">
              <div className="mr-3 mt-1 h-9 w-9 shrink-0 rounded-full bg-violet-500 text-white">
                <div className="flex h-full items-center justify-center text-xs font-semibold">
                  {agentName.replace('Agent', '').slice(0, 2).toUpperCase()}
                </div>
              </div>
              <div className="min-w-0 grow border-b border-gray-100 pb-3 text-sm last:border-b-0">
                <div className="flex items-center justify-between gap-3">
                  <div className="truncate font-medium text-gray-900">{message}</div>
                  <div className="shrink-0 text-xs text-gray-400">{formatRelativeTime(timestamp)}</div>
                </div>
                <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-gray-500">
                  <span className="inline-flex rounded-full bg-sky-100 px-2 py-1 font-semibold text-sky-700">{agentName}</span>
                  <span>Story</span>
                  {workItemId ? (
                    <Link to={`/story/${workItemId}`} className="font-semibold text-violet-500 hover:text-violet-600">
                      #{workItemId}
                    </Link>
                  ) : (
                    <span className="font-semibold text-gray-400">N/A</span>
                  )}
                </div>
              </div>
            </div>
            );
          })
        ) : (
          <div className="rounded-lg border border-dashed border-gray-200 px-4 py-8 text-center text-sm text-gray-500">
            No recent activity yet.
          </div>
        )}
      </div>
    </div>
  );
}
