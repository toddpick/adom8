const apiBaseUrl = process.env.NEXT_PUBLIC_ONBOARDING_API_BASE_URL ?? '';
const functionKey = process.env.NEXT_PUBLIC_ONBOARDING_FUNCTION_KEY ?? '';

export interface ProjectRegistrationPayload {
  displayName: string;
  adoOrg: string;
  adoProject: string;
  adoPat: string;
  githubRepo: string;
  githubPat: string;
  primaryBranch: string;
  defaultAutonomyLevel: number;
  capabilities: {
    selfHealing: boolean;
    playwrightTesting: boolean;
  };
}

export interface RegistrationResponse {
  provisioningId: string;
  statusUrl: string;
  projectId: string;
}

export interface ProvisioningStatus {
  provisioningId: string;
  stage: string;
  progressPercent: number;
  runtimeStatus: string;
  error?: string;
}

function buildApiUrl(path: string) {
  const base = apiBaseUrl || window.location.origin;
  const url = new URL(`/api/${path.replace(/^\/+/, '')}`, base);
  if (functionKey) {
    url.searchParams.set('code', functionKey);
  }

  return url;
}

export async function registerProject(payload: ProjectRegistrationPayload): Promise<RegistrationResponse> {
  const response = await fetch(buildApiUrl('projects/register'), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    throw new Error(await readError(response));
  }

  return response.json();
}

export async function getProvisioningStatus(provisioningId: string): Promise<ProvisioningStatus> {
  const response = await fetch(buildApiUrl(`projects/${provisioningId}/status`), {
    cache: 'no-store'
  });

  if (!response.ok) {
    throw new Error(await readError(response));
  }

  return response.json();
}

async function readError(response: Response) {
  const text = await response.text();
  if (!text) {
    return `Request failed with HTTP ${response.status}`;
  }

  try {
    const body = JSON.parse(text);
    return body.error ?? body.errors?.join?.(' ') ?? text;
  } catch {
    return text;
  }
}
