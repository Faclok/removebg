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

export type Health = {
  status: string;
  database?: string;
  model?: string;
  device?: string;
  workers?: number;
  intraOpThreads?: number;
  queueCapacity?: number;
  active?: number;
  queued?: number;
};

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly requestId?: string,
    public readonly retryAfterSeconds?: number,
  ) {
    super(message);
  }
}

async function parseError(response: Response): Promise<ApiError> {
  const retryAfter = Number(response.headers.get('Retry-After'));
  const retryAfterSeconds = Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : undefined;
  try {
    const body = (await response.json()) as {
      error?: { code?: string; message?: string; requestId?: string };
    };
    return new ApiError(
      response.status,
      body.error?.code ?? 'request_failed',
      body.error?.message ?? `Ошибка HTTP ${response.status}`,
      body.error?.requestId,
      retryAfterSeconds,
    );
  } catch {
    return new ApiError(
      response.status,
      'request_failed',
      `Ошибка HTTP ${response.status}`,
      undefined,
      retryAfterSeconds,
    );
  }
}

function delay(milliseconds: number) {
  return new Promise<void>((resolve) => window.setTimeout(resolve, milliseconds));
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
  usage: (apiKey: string, limit = 50, offset = 0) =>
    json<Usage>(`/v1/usage?limit=${limit}&offset=${offset}`, {}, apiKey),
  keys: (apiKey: string) => json<ApiKeyView[]>('/v1/api-keys', {}, apiKey),
  health: () => json<Health>('/health/ready'),

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
    for (let attempt = 0; attempt < 8; attempt++) {
      const data = new FormData();
      data.append('image', file);
      const response = await fetch('/v1/remove-background', {
        method: 'POST',
        headers: { 'X-API-Key': apiKey },
        body: data,
      });
      if (response.ok) {
        return {
          blob: await response.blob(),
          chargedRub: response.headers.get('X-Charged-Rub'),
          balanceRub: response.headers.get('X-Balance-Rub'),
        };
      }

      const error = await parseError(response);
      const retryable = error.code === 'server_busy' || error.code === 'model_warming_up';
      if (!retryable || attempt === 7) throw error;
      await delay((error.retryAfterSeconds ?? 2 ** attempt) * 1000);
    }
    throw new ApiError(503, 'processing_failed', 'Не удалось обработать изображение');
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
