export function LiveLinks({
  dashboardUrl,
  initStoryUrl,
  agentTabUrl
}: {
  dashboardUrl: string;
  initStoryUrl: string;
  agentTabUrl: string;
}) {
  return (
    <section className="mt-6 rounded border p-4">
      <h2 className="font-medium">Live Links</h2>
      <ul className="mt-2 list-disc pl-6 text-sm">
        <li><a className="text-blue-600" href={agentTabUrl}>GitHub Copilot Agent Tab</a></li>
        <li><a className="text-blue-600" href={initStoryUrl}>ADO Init Story</a></li>
        <li><a className="text-blue-600" href={dashboardUrl}>Project Dashboard</a></li>
      </ul>
    </section>
  );
}
