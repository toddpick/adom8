import { LiveLinks } from '../../../components/LiveLinks';

export default function DashboardPage({ params }: { params: { projectId: string } }) {
  return (
    <main className="mx-auto max-w-5xl p-8">
      <h1 className="text-2xl font-semibold">Project Dashboard</h1>
      <p className="mt-2 text-sm text-gray-600">Project ID: {params.projectId}</p>
      <LiveLinks
        agentTabUrl="#"
        initStoryUrl="#"
        dashboardUrl={`https://dashboard.adom8.dev/${params.projectId}`}
      />
    </main>
  );
}
