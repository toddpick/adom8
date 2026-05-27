import { useEffect, useMemo, useState } from 'react';
import { Link, useLocation, useOutletContext, useParams } from 'react-router-dom';

import { getStoryDetail } from '../api';
import { buildFallbackStoryDetail, mergeStoryDetailWithLiveStatus } from '../utils/story';
import { formatDuration, formatRelativeTime, formatTimestamp } from '../utils/formatting';

function phaseStyles(status) {
  switch (status) {
    case 'completed':
      return 'bg-green-100 text-green-700 border-green-200';
    case 'active':
      return 'bg-sky-100 text-sky-700 border-sky-300 shadow-[0_0_0_1px_rgba(56,189,248,0.2)]';
    case 'failed':
      return 'bg-red-100 text-red-700 border-red-200';
    default:
      return 'bg-gray-100 text-gray-600 border-gray-200';
  }
}

function formatAgentTokenValue(phase, metadata) {
  const isCopilotCoding = phase.agent === 'CodingAgent'
    && (phase.details?.additionalData?.mode === 'copilot-delegated' || metadata.githubDelegated);

  if (isCopilotCoding) {
    return '1 premium token';
  }

  const totalTokens = phase.tokenUsage?.totalTokens;
  if (typeof totalTokens === 'number' && totalTokens >= 0) {
    return totalTokens.toLocaleString();
  }

  return '—';
}

function formatAgentCostValue(phase, metadata) {
  const isCopilotCoding = phase.agent === 'CodingAgent'
    && (phase.details?.additionalData?.mode === 'copilot-delegated' || metadata.githubDelegated);

  if (isCopilotCoding) {
    return '—';
  }

  const estimatedCost = phase.tokenUsage?.estimatedCost;
  if (typeof estimatedCost === 'number') {
    return `$${estimatedCost.toFixed(4)}`;
  }

  return '—';
}

function formatAgentDurationValue(phase) {
  return phase.durationSeconds == null ? '—' : formatDuration(phase.durationSeconds);
}

function GitHubMark({ className = '', style }) {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" className={className} style={style} fill="currentColor">
      <path d="M8 0C3.58 0 0 3.67 0 8.2c0 3.62 2.29 6.69 5.47 7.77.4.08.55-.18.55-.39 0-.19-.01-.83-.01-1.5-2.01.38-2.53-.5-2.69-.95-.09-.24-.48-.95-.82-1.14-.28-.16-.68-.55-.01-.56.63-.01 1.08.59 1.23.84.72 1.25 1.87.9 2.33.69.07-.54.28-.9.51-1.11-1.78-.21-3.64-.92-3.64-4.07 0-.9.31-1.63.82-2.2-.08-.21-.36-1.05.08-2.18 0 0 .67-.22 2.2.84a7.35 7.35 0 0 1 4 0c1.53-1.06 2.2-.84 2.2-.84.44 1.13.16 1.97.08 2.18.51.57.82 1.29.82 2.2 0 3.16-1.87 3.86-3.66 4.07.29.26.54.75.54 1.52 0 1.1-.01 1.99-.01 2.26 0 .21.14.47.55.39A8.24 8.24 0 0 0 16 8.2C16 3.67 12.42 0 8 0Z" />
    </svg>
  );
}

export default function StoryDetail() {
  const { id } = useParams();
  const location = useLocation();
  const { appKey, data: statusData } = useOutletContext();
  const [detail, setDetail] = useState(() => buildFallbackStoryDetail(id, statusData, location.state?.story));
  const [detailsUnavailable, setDetailsUnavailable] = useState(false);
  const [loading, setLoading] = useState(true);
  const numericId = Number(id);
  const liveStatusDetail = useMemo(
    () => buildFallbackStoryDetail(id, statusData, location.state?.story),
    [id, location.state?.story, statusData],
  );

  useEffect(() => {
    let cancelled = false;

    async function loadDetail() {
      setLoading(true);
      setDetailsUnavailable(false);

      try {
        const response = await getStoryDetail(id, appKey);

        if (cancelled) {
          return;
        }

        if (!response) {
          setDetailsUnavailable(true);
          return;
        }

        setDetail(response);
      } catch (error) {
        if (cancelled) {
          return;
        }

        if (error.code === 404) {
          setDetailsUnavailable(true);
          return;
        }

        setDetailsUnavailable(true);
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    loadDetail();

    return () => {
      cancelled = true;
    };
  }, [appKey, id]);

  const metadata = useMemo(() => {
    const stableDetail = detail?.id === numericId ? detail : null;
    return mergeStoryDetailWithLiveStatus(stableDetail, liveStatusDetail);
  }, [detail, liveStatusDetail, numericId]);

  if (loading && !metadata) {
    return <div className="rounded-xl bg-white px-5 py-8 text-sm text-gray-500 shadow-xs">Loading story details...</div>;
  }

  if (!metadata) {
    return <div className="rounded-xl border border-dashed border-gray-200 bg-white px-5 py-8 text-sm text-gray-500 shadow-xs">Story details not found.</div>;
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to="/" className="text-sm font-medium text-violet-500 hover:text-violet-600">
            Back to Overview
          </Link>
          <h2 className="mt-2 text-2xl font-semibold text-gray-900">
            #{metadata.id} {metadata.title}
          </h2>
        </div>
        <div className="flex flex-wrap gap-2 text-sm">
          <span className="rounded-full bg-gray-100 px-3 py-1 font-semibold text-gray-700">{metadata.state}</span>
          {metadata.currentAgent ? (
            <span className="rounded-full bg-sky-100 px-3 py-1 font-semibold text-sky-700">{metadata.currentAgent}</span>
          ) : null}
          {metadata.autonomyLevel != null ? (
            <span className="rounded-full bg-violet-100 px-3 py-1 font-semibold text-violet-700">Autonomy L{metadata.autonomyLevel}</span>
          ) : null}
        </div>
      </div>

      {detailsUnavailable ? (
        <div className="rounded-xl border border-yellow-200 bg-yellow-50 px-4 py-3 text-sm text-yellow-700">
          Details not available from the backend yet. Showing the best available status snapshot.
        </div>
      ) : null}

      <div className="grid grid-cols-12 gap-6">
        <div className="col-span-full rounded-xl bg-white p-5 shadow-xs xl:col-span-4">
          <h3 className="text-sm font-semibold uppercase tracking-[0.16em] text-gray-400">Story Summary</h3>
          <dl className="mt-4 space-y-4 text-sm">
            <div>
              <dt className="text-gray-400">Current Agent</dt>
              <dd className="mt-1 font-semibold text-gray-900">{metadata.currentAgent ?? 'Pending assignment'}</dd>
            </div>
            <div>
              <dt className="text-gray-400">Work Item State</dt>
              <dd className="mt-1 font-semibold text-gray-900">{metadata.workItemState ?? metadata.state ?? 'Unknown'}</dd>
            </div>
            <div>
              <dt className="text-gray-400">Last Agent</dt>
              <dd className="mt-1 font-semibold text-gray-900">{metadata.lastAgent ?? 'None yet'}</dd>
            </div>
            <div>
              <dt className="text-gray-400">Created</dt>
              <dd className="mt-1 font-semibold text-gray-900">{formatTimestamp(metadata.createdDate)}</dd>
            </div>
            <div>
              <dt className="text-gray-400">Updated</dt>
              <dd className="mt-1 font-semibold text-gray-900">{formatTimestamp(metadata.updatedDate)}</dd>
            </div>
          </dl>
          {metadata.githubDelegated && metadata.githubIssueUrl ? (
            <div className="mt-5 rounded-xl border border-sky-200 bg-sky-50 px-4 py-4">
              <div className="text-xs font-semibold uppercase tracking-[0.14em] text-sky-700">GitHub Delegation</div>
              <p className="mt-2 text-sm text-sky-900">
                Coding is currently delegated to @{metadata.delegatedAgent ?? 'copilot'}.
              </p>
              <a
                href={metadata.githubIssueUrl}
                target="_blank"
                rel="noreferrer"
                className="mt-3 inline-flex rounded-full bg-sky-600 px-3 py-1.5 text-xs font-semibold text-white transition hover:bg-sky-700"
              >
                Open GitHub Issue #{metadata.githubIssueNumber}
              </a>
            </div>
          ) : null}
        </div>

        <div className="col-span-full rounded-xl bg-white p-5 shadow-xs xl:col-span-8">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="font-semibold text-gray-800">Agent Phase Timeline</h3>
            <span className="text-sm text-gray-400">Latest update {formatRelativeTime(metadata.updatedDate)}</span>
          </div>
          <ul className="space-y-4">
            {(metadata.phases ?? []).map((phase, index) => (
              <li
                key={phase?.agent ?? `phase-${index}`}
                className={`relative rounded-xl pb-4 pl-2 pr-2 pt-1 last:pb-0 ${phase.status === 'active' ? 'bg-sky-50/70' : ''}`}
              >
                <div className="pl-8">
                  <div className="flex flex-wrap items-center justify-between gap-3">
                    <div>
                      <div className="text-sm font-semibold text-gray-900">{String(phase?.agent ?? 'UnknownAgent').replace('Agent', ' Agent')}</div>
                      <div className="mt-1 text-xs text-gray-500">
                        {phase.status === 'active'
                          ? 'Processing now'
                          : phase.completedAt
                            ? `Completed ${formatRelativeTime(phase.completedAt)}`
                            : 'Awaiting execution'}
                      </div>
                      {phase.rawStatus ? (
                        <div className="mt-1 text-[11px] font-semibold uppercase tracking-[0.12em] text-gray-400">
                          Backend: {phase.rawStatus}
                        </div>
                      ) : null}
                      <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-gray-500">
                        <span>Tokens: {formatAgentTokenValue(phase, metadata)}</span>
                        <span>Cost: {formatAgentCostValue(phase, metadata)}</span>
                        <span>Duration: {formatAgentDurationValue(phase)}</span>
                      </div>
                      {phase.agent === 'CodingAgent' && metadata.githubDelegated && phase.status === 'active' ? (
                        <div className="mt-3 inline-flex max-w-full items-center gap-3 rounded-2xl border border-sky-200 bg-white/90 px-3 py-2 text-xs shadow-sm ring-1 ring-sky-100">
                          <span className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-gray-900 text-white shadow-sm">
                            <GitHubMark className="h-4 w-4 animate-spin" style={{ animationDuration: '2.8s' }} />
                          </span>
                          <div className="min-w-0">
                            <div className="font-semibold uppercase tracking-[0.14em] text-sky-700">Delegated To GitHub Copilot</div>
                            <div className="mt-0.5 text-gray-600">
                              Coding is running in the GitHub agent workspace.
                            </div>
                          </div>
                          {metadata.githubAgentsUrl ? (
                            <a
                              href={metadata.githubAgentsUrl}
                              target="_blank"
                              rel="noreferrer"
                              className="shrink-0 rounded-full bg-gray-900 px-3 py-1.5 font-semibold text-white transition hover:bg-gray-800"
                            >
                              Open /agents
                            </a>
                          ) : null}
                        </div>
                      ) : null}
                      {phase.agent === 'CodingAgent' && metadata.githubDelegated && metadata.githubIssueUrl && phase.status === 'active' ? (
                        <a
                          href={metadata.githubIssueUrl}
                          target="_blank"
                          rel="noreferrer"
                          className="mt-2 inline-flex text-xs font-semibold text-sky-700 hover:text-sky-800"
                        >
                          View GitHub agent issue #{metadata.githubIssueNumber}
                        </a>
                      ) : null}
                    </div>
                    <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold capitalize ${phaseStyles(phase.status)}`}>
                      {phase.status}
                    </span>
                  </div>
                </div>
                <div aria-hidden="true">
                  {index < (metadata.phases?.length ?? 0) - 1 ? (
                    <div className="absolute bottom-0 left-1.5 top-0.5 ml-px w-0.5 bg-gray-200" />
                  ) : null}
                  {phase.status === 'active' ? (
                    <>
                      <div className="absolute left-0 top-1.5 h-3 w-3 rounded-full bg-sky-400 opacity-75 animate-ping" />
                      <div className="absolute left-0 top-1.5 h-3 w-3 rounded-full border-2 border-white bg-sky-500" />
                    </>
                  ) : (
                    <div className={`absolute left-0 top-1.5 h-3 w-3 rounded-full border-2 border-white ${phase.status === 'failed' ? 'bg-red-500' : phase.status === 'completed' ? 'bg-green-500' : 'bg-violet-500'}`} />
                  )}
                </div>
              </li>
            ))}
          </ul>
        </div>

        <div className="col-span-full rounded-xl bg-white shadow-xs">
          <header className="border-b border-gray-100 px-5 py-4">
            <h3 className="font-semibold text-gray-800">Story Activity</h3>
          </header>
          <div className="p-3">
            {(metadata.activity ?? []).length ? (
              <div className="overflow-x-auto">
                <table className="table-auto w-full">
                  <thead className="rounded-xs bg-gray-50 text-xs uppercase text-gray-400">
                    <tr>
                      <th className="p-2 text-left font-semibold">Timestamp</th>
                      <th className="p-2 text-left font-semibold">Agent</th>
                      <th className="p-2 text-left font-semibold">Message</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100 text-sm">
                    {metadata.activity.map((entry, index) => (
                      <tr key={`${entry.timestamp}-${index}`}>
                        <td className="p-2 text-gray-500">{formatTimestamp(entry.timestamp)}</td>
                        <td className="p-2 font-medium text-gray-900">{entry.agent}</td>
                        <td className="p-2 text-gray-700">{entry.message}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <div className="rounded-lg border border-dashed border-gray-200 px-4 py-8 text-center text-sm text-gray-500">
                No activity is available for this story yet.
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
