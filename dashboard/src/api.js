import { config } from './config';
import { getMockStatusPayload, getMockStoryDetail } from './mockData';

const STATUS_ENDPOINTS = ['/api/status', '/api/GetCurrentStatus'];
const STORY_DETAIL_ENDPOINTS = ['/api/GetStoryDetail'];
const HEALTH_ENDPOINT = '/api/health';
const CODEBASE_INTELLIGENCE_ENDPOINT = '/api/codebase-intelligence';
const INITIALIZE_CODEBASE_ENDPOINT = '/api/initialize-codebase';
const CLEAR_STORIES_ENDPOINT = '/api/clear-stories';
const CLEAR_ACTIVITY_ENDPOINT = '/api/clear-activity';
const VALIDATE_DASHBOARD_KEY_ENDPOINT = '/api/validate-dashboard-key';

function getBaseOrigin() {
  if (config.apiBaseUrl) {
    return config.apiBaseUrl;
  }

  return window.location.origin;
}

function appendAuthParams(url, appKey) {
  if (!appKey) {
    return url;
  }

  url.searchParams.set('appKey', appKey);
  url.searchParams.set('code', appKey);
  return url;
}

function buildUrl(path, appKey, query = {}) {
  const url = new URL(path, getBaseOrigin());
  Object.entries(query).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') {
      url.searchParams.set(key, String(value));
    }
  });

  return appendAuthParams(url, appKey);
}

async function sendJsonRequest(path, appKey, { method = 'GET', query, body } = {}) {
  const headers = {
    Accept: 'application/json',
  };

  const requestInit = {
    method,
    headers,
    cache: 'no-store',
  };

  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    requestInit.body = typeof body === 'string' ? body : JSON.stringify(body);
  }

  const response = await fetch(buildUrl(path, appKey, query), requestInit);

  if (response.status === 401 || response.status === 403) {
    const unauthorizedError = new Error('Unauthorized');
    unauthorizedError.code = response.status;
    throw unauthorizedError;
  }

  if (response.status === 404) {
    const notFoundError = new Error('Not Found');
    notFoundError.code = 404;
    throw notFoundError;
  }

  if (!response.ok) {
    const errorBody = await response.text();
    let parsedErrorBody = null;
    try {
      parsedErrorBody = errorBody ? JSON.parse(errorBody) : null;
    } catch {
      parsedErrorBody = null;
    }

    const requestError = new Error(
      parsedErrorBody?.message
      || parsedErrorBody?.status
      || errorBody
      || `Request failed with status ${response.status}`,
    );
    requestError.code = response.status;
    requestError.responseData = parsedErrorBody;
    throw requestError;
  }

  const responseText = await response.text();
  if (!responseText) {
    return null;
  }

  try {
    return JSON.parse(responseText);
  } catch {
    return responseText;
  }
}

async function fetchJsonFromCandidates(paths, appKey, query) {
  let lastError = null;

  for (const path of paths) {
    try {
      return await sendJsonRequest(path, appKey, { method: 'GET', query });
    } catch (error) {
      if (error.code === 404) {
        lastError = error;
        continue;
      }

      throw error;
    }
  }

  throw lastError ?? new Error('Request failed');
}

export async function validateAppKey(appKey) {
  const trimmed = String(appKey || '').trim();
  if (!trimmed) {
    return false;
  }

  if (config.useMockData) {
    return true;
  }

  try {
    await sendJsonRequest(VALIDATE_DASHBOARD_KEY_ENDPOINT, trimmed);
    return true;
  } catch (error) {
    if (error.code === 401 || error.code === 403 || error.code === 404) {
      return false;
    }

    throw error;
  }
}

export async function getCurrentStatus(appKey) {
  if (config.useMockData) {
    return getMockStatusPayload();
  }

  return fetchJsonFromCandidates(STATUS_ENDPOINTS, appKey);
}

export async function getStoryDetail(storyId, appKey) {
  if (config.useMockData) {
    return getMockStoryDetail(storyId);
  }

  return fetchJsonFromCandidates(STORY_DETAIL_ENDPOINTS, appKey, { id: storyId });
}

export async function getSystemHealth(appKey) {
  return sendJsonRequest(HEALTH_ENDPOINT, appKey);
}

export async function getCodebaseIntelligence(appKey) {
  return sendJsonRequest(CODEBASE_INTELLIGENCE_ENDPOINT, appKey);
}

export async function initializeCodebase(appKey) {
  return sendJsonRequest(INITIALIZE_CODEBASE_ENDPOINT, appKey, {
    method: 'POST',
    body: {},
  });
}

export async function clearStories(appKey) {
  return sendJsonRequest(CLEAR_STORIES_ENDPOINT, appKey, {
    method: 'POST',
    body: {},
  });
}

export async function clearActivity(appKey) {
  return sendJsonRequest(CLEAR_ACTIVITY_ENDPOINT, appKey, {
    method: 'POST',
    body: {},
  });
}
