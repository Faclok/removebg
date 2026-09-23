export type PricingTier = {
  fromImages: number;
  toImages: number | null;
  priceRub: number;
};

export type RegisterResponse = {
  accountId: string;
  apiKey: string;
  balanceRub: number;
  message: string;
};

export type Balance = {
  accountId: string;
  balanceRub: number;
  successfulImagesThisMonth: number;
  nextImagePriceRub: number;
  pricing: PricingTier[];
};

export type UsageItem = {
  id: string;
  requestId: string;
  status: string;
  filename: string | null;
  inputBytes: number;
  outputBytes: number;
  priceRub: number;
  durationMs: number;
  errorCode: string | null;
  createdAt: string;
};

export type Usage = {
  total: number;
  successful: number;
  chargedRub: number;
  items: UsageItem[];
};

export type ApiKeyView = {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
};

export type ApiKeyCreated = ApiKeyView & { apiKey: string };

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly requestId?: string,
  ) {
    super(message);
  }
}

async function parseError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as {
      error?: { code?: string; message?: string; requestId?: string };
    };
    return new ApiError(
      response.status,
      body.error?.code ?? 'request_failed',
      body.error?.message ?? `Ошибка HTTP ${response.status}`,
      body.error?.requestId,
    );
  } catch {
    return new ApiError(response.status, 'request_failed', `Ошибка HTTP ${response.status}`);
  }
}

async function json<T>(path: string, init: RequestInit = {}, apiKey?: string): Promise<T> {
  const headers = new Headers(init.headers);
  if (apiKey) headers.set('X-API-Key', apiKey);
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json');

  const response = await fetch(path, { ...init, headers });
  if (!response.ok) throw await parseError(response);
  return (await response.json()) as T;
}

export const api = {
  register: (name: string, email: string) =>
    json<RegisterResponse>('/v1/accounts/register', {
      method: 'POST',
      body: JSON.stringify({ name, email }),
    }),

  balance: (apiKey: string) => json<Balance>('/v1/balance', {}, apiKey),
  usage: (apiKey: string) => json<Usage>('/v1/usage?limit=50', {}, apiKey),
  keys: (apiKey: string) => json<ApiKeyView[]>('/v1/api-keys', {}, apiKey),

  createKey: (apiKey: string, name: string) =>
    json<ApiKeyCreated>(
      '/v1/api-keys',
      { method: 'POST', body: JSON.stringify({ name }) },
      apiKey,
    ),

  revokeKey: async (apiKey: string, id: string) => {
    const response = await fetch(`/v1/api-keys/${id}`, {
      method: 'DELETE',
      headers: { 'X-API-Key': apiKey },
    });
    if (!response.ok) throw await parseError(response);
  },

  removeBackground: async (apiKey: string, file: File) => {
    const data = new FormData();
    data.append('image', file);
    const response = await fetch('/v1/remove-background', {
      method: 'POST',
      headers: { 'X-API-Key': apiKey },
      body: data,
    });
    if (!response.ok) throw await parseError(response);
    return {
      blob: await response.blob(),
      chargedRub: response.headers.get('X-Charged-Rub'),
      balanceRub: response.headers.get('X-Balance-Rub'),
    };
  },

  topUp: (accountId: string, token: string, amountRub: number, description: string) =>
    json<{ accountId: string; addedRub: number; balanceRub: number }>(
      `/v1/admin/accounts/${accountId}/top-up`,
      {
        method: 'POST',
        headers: { 'X-Admin-Token': token },
        body: JSON.stringify({ amountRub, description }),
      },
    ),
};
