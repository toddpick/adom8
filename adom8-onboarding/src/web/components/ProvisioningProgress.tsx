'use client';

export function ProvisioningProgress({ provisioningId }: { provisioningId: string }) {
  return (
    <section className="mt-6 rounded border p-4">
      <p className="text-sm text-gray-600">Provisioning ID: {provisioningId}</p>
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
