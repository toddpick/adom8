import { OnboardingForm } from '../../components/OnboardingForm';

export default function OnboardPage() {
  return (
    <main className="mx-auto max-w-4xl p-8">
      <h1 className="text-2xl font-semibold">ADOm8 Managed Onboarding</h1>
      <p className="mt-2 text-sm text-gray-600">Register an ADO project + GitHub repo for managed ADOm8 provisioning.</p>
      <OnboardingForm />
    </main>
  );
}
