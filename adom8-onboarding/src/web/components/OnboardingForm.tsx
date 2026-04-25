'use client';

export function OnboardingForm() {
  return (
    <form className="mt-6 grid gap-4">
      <input className="rounded border p-2" placeholder="ADO Organization URL" />
      <input className="rounded border p-2" placeholder="ADO Project Name" />
      <input className="rounded border p-2" placeholder="ADO PAT" type="password" />
      <input className="rounded border p-2" placeholder="GitHub Repository URL" />
      <input className="rounded border p-2" placeholder="GitHub PAT" type="password" />
      <button className="rounded bg-black px-4 py-2 text-white" type="submit">Start Onboarding</button>
    </form>
  );
}
