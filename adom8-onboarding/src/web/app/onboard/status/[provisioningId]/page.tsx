import { ProvisioningProgress } from '../../../../components/ProvisioningProgress';

export default function OnboardStatusPage({ params }: { params: { provisioningId: string } }) {
  return (
    <main className="mx-auto max-w-4xl p-8">
      <h1 className="text-2xl font-semibold">Provisioning Status</h1>
      <ProvisioningProgress provisioningId={params.provisioningId} />
    </main>
  );
}
