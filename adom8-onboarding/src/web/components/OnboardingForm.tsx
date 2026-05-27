'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { registerProject } from '../lib/onboardingApi';

export function OnboardingForm() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);

    const form = new FormData(event.currentTarget);
    try {
      const result = await registerProject({
        displayName: String(form.get('displayName') ?? ''),
        adoOrg: String(form.get('adoOrg') ?? ''),
        adoProject: String(form.get('adoProject') ?? ''),
        adoPat: String(form.get('adoPat') ?? ''),
        githubRepo: String(form.get('githubRepo') ?? ''),
        githubPat: String(form.get('githubPat') ?? ''),
        primaryBranch: String(form.get('primaryBranch') ?? 'main') || 'main',
        defaultAutonomyLevel: Number(form.get('defaultAutonomyLevel') ?? 3),
        capabilities: {
          selfHealing: form.get('selfHealing') === 'on',
          playwrightTesting: form.get('playwrightTesting') === 'on'
        }
      });

      router.push(`/onboard/status/${result.provisioningId}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Registration failed.');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form className="mt-6 grid gap-4" onSubmit={handleSubmit}>
      <input className="rounded border p-2" name="displayName" placeholder="Display Name" required />
      <input className="rounded border p-2" name="adoOrg" placeholder="ADO Organization URL or name" required />
      <input className="rounded border p-2" name="adoProject" placeholder="ADO Project Name" required />
      <input className="rounded border p-2" name="adoPat" placeholder="ADO PAT" type="password" required />
      <input className="rounded border p-2" name="githubRepo" placeholder="GitHub Repository URL or owner/repo" required />
      <input className="rounded border p-2" name="githubPat" placeholder="GitHub PAT" type="password" required />
      <input className="rounded border p-2" name="primaryBranch" placeholder="Primary branch" defaultValue="main" />
      <select className="rounded border p-2" name="defaultAutonomyLevel" defaultValue="3">
        <option value="1">Autonomy 1 - Plan only</option>
        <option value="2">Autonomy 2 - Code only</option>
        <option value="3">Autonomy 3 - Review and pause</option>
        <option value="4">Autonomy 4 - Auto-merge</option>
        <option value="5">Autonomy 5 - Full autonomy</option>
      </select>
      <label className="flex items-center gap-2 text-sm">
        <input name="selfHealing" type="checkbox" defaultChecked />
        Enable shared self-healing topic
      </label>
      <label className="flex items-center gap-2 text-sm">
        <input name="playwrightTesting" type="checkbox" />
        Enable Playwright test setup
      </label>
      {error && <p className="rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</p>}
      <button className="rounded bg-black px-4 py-2 text-white disabled:opacity-60" type="submit" disabled={submitting}>
        {submitting ? 'Starting...' : 'Start Onboarding'}
      </button>
    </form>
  );
}
