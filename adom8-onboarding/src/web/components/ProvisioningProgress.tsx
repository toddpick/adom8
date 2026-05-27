'use client';

import { useEffect, useState } from 'react';
import { getProvisioningStatus, type ProvisioningStatus } from '../lib/onboardingApi';

export function ProvisioningProgress({ provisioningId }: { provisioningId: string }) {
  const [status, setStatus] = useState<ProvisioningStatus | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      try {
        const nextStatus = await getProvisioningStatus(provisioningId);
        if (!cancelled) {
          setStatus(nextStatus);
          setError(null);
        }
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Unable to load provisioning status.');
        }
      }
    }

    load();
    const timer = window.setInterval(load, 5000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [provisioningId]);

  const progress = status?.progressPercent ?? 0;

  return (
    <section className="mt-6 rounded border p-4">
      <p className="text-sm text-gray-600">Provisioning ID: {provisioningId}</p>
      <div className="mt-4 h-3 overflow-hidden rounded bg-gray-200">
        <div className="h-full bg-black transition-all" style={{ width: `${progress}%` }} />
      </div>
      <p className="mt-2 text-sm">Status: {status?.runtimeStatus ?? 'Loading...'}</p>
      {error && <p className="mt-2 rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</p>}
      <ul className="mt-3 list-disc pl-6 text-sm">
        <li>Credentials validation</li>
        <li>Secret storage</li>
        <li>Infrastructure deployment</li>
        <li>ADO configuration</li>
        <li>Codebase context + init story</li>
      </ul>
    </section>
  );
}
