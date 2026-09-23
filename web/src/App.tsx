import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type DragEvent,
  type ReactNode,
} from 'react';
import {
  api,
  ApiError,
  type ApiKeyCreated,
  type ApiKeyView,
  type Balance,
  type Usage,
} from './api';

const STORAGE_KEY = 'udalifon_api_key';

type LooseStyle = CSSProperties & Record<string, string | number | undefined>;
type StyleValue = any;

function flattenStyle(style: StyleValue | StyleValue[] | ((state: { pressed: boolean }) => StyleValue | StyleValue[]), pressed = false) {
  const resolved = typeof style === 'function' ? style({ pressed }) : style;
  const flat = (Array.isArray(resolved) ? Object.assign({}, ...resolved.filter(Boolean)) : resolved || {}) as LooseStyle;
  const normalized: LooseStyle = { ...flat };
  if (flat.marginHorizontal !== undefined) {
    normalized.marginLeft = flat.marginHorizontal;
    normalized.marginRight = flat.marginHorizontal;
    delete normalized.marginHorizontal;
  }
  if (flat.marginVertical !== undefined) {
    normalized.marginTop = flat.marginVertical;
    normalized.marginBottom = flat.marginVertical;
    delete normalized.marginVertical;
  }
  if (flat.paddingHorizontal !== undefined) {
    normalized.paddingLeft = flat.paddingHorizontal;
    normalized.paddingRight = flat.paddingHorizontal;
    delete normalized.paddingHorizontal;
  }
  if (flat.paddingVertical !== undefined) {
    normalized.paddingTop = flat.paddingVertical;
    normalized.paddingBottom = flat.paddingVertical;
    delete normalized.paddingVertical;
  }
  if (flat.borderWidth !== undefined && !flat.borderStyle) normalized.borderStyle = 'solid';
  if (flat.borderTopWidth !== undefined && !flat.borderTopStyle) normalized.borderTopStyle = 'solid';
  if (typeof flat.lineHeight === 'number') normalized.lineHeight = `${flat.lineHeight}px`;
  return normalized as CSSProperties;
}

function View({ children, style, ...props }: { children?: ReactNode; style?: StyleValue } & Omit<React.HTMLAttributes<HTMLDivElement>, 'style'>) {
  return <div style={{ display: 'flex', flexDirection: 'column', ...flattenStyle(style) }} {...props}>{children}</div>;
}

function Text({ children, style, numberOfLines, ...props }: { children?: ReactNode; style?: StyleValue; numberOfLines?: number } & Omit<React.HTMLAttributes<HTMLSpanElement>, 'style'>) {
  const clipped = numberOfLines ? { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' } as CSSProperties : undefined;
  return <span style={{ ...flattenStyle(style), ...clipped }} {...props}>{children}</span>;
}

function Pressable({ children, style, onPress, disabled, ...props }: {
  children?: ReactNode;
  style?: StyleValue | StyleValue[] | ((state: { pressed: boolean }) => StyleValue | StyleValue[]);
  onPress?: () => void;
  disabled?: boolean;
} & Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, 'style' | 'onClick'>) {
  const [pressed, setPressed] = useState(false);
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onPress}
      onPointerDown={() => setPressed(true)}
      onPointerUp={() => setPressed(false)}
      onPointerLeave={() => setPressed(false)}
      style={{ display: 'flex', flexDirection: 'column', ...flattenStyle(style, pressed) }}
      {...props}
    >
      {children}
    </button>
  );
}

function ScrollView({ children, style }: { children?: ReactNode; style?: StyleValue | StyleValue[] }) {
  return <div style={{ ...flattenStyle(style), overflowY: 'auto' }}>{children}</div>;
}

function ActivityIndicator({ size = 'small' }: { color?: string; size?: 'small' | 'large' }) {
  return <span className={`spinner spinner-${size}`} aria-label="Загрузка" />;
}

const StyleSheet = { create: <T extends Record<string, LooseStyle>>(styles: T) => styles };

function money(value: number) {
  return new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB' }).format(value);
}

function date(value: string | null) {
  if (!value) return '—';
  return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'short', timeStyle: 'short' }).format(
    new Date(value),
  );
}

function bytes(value: number) {
  if (value < 1024) return `${value} Б`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} КБ`;
  return `${(value / 1024 / 1024).toFixed(1)} МБ`;
}

function messageOf(error: unknown) {
  if (error instanceof ApiError) {
    return `${error.message}${error.requestId ? ` · запрос ${error.requestId}` : ''}`;
  }
  return error instanceof Error ? error.message : 'Неизвестная ошибка';
}

function Button({
  children,
  onPress,
  disabled = false,
  secondary = false,
  danger = false,
}: {
  children: ReactNode;
  onPress: () => void;
  disabled?: boolean;
  secondary?: boolean;
  danger?: boolean;
}) {
  return (
    <Pressable
      aria-label={typeof children === 'string' ? children : undefined}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }: { pressed: boolean }) => [
        styles.button,
        secondary && styles.buttonSecondary,
        danger && styles.buttonDanger,
        (pressed || disabled) && styles.buttonMuted,
      ]}
    >
      <Text style={[styles.buttonText, secondary && styles.buttonSecondaryText]}>{children}</Text>
    </Pressable>
  );
}

function Field({
  label,
  value,
  onChange,
  type = 'text',
  placeholder,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  type?: string;
  placeholder?: string;
}) {
  return (
    <label className="field">
      <span>{label}</span>
      <input
        type={type}
        value={value}
        placeholder={placeholder}
        onChange={(event) => onChange(event.target.value)}
      />
    </label>
  );
}

function Notice({ text, kind = 'error' }: { text: string; kind?: 'error' | 'success' }) {
  return (
    <View style={[styles.notice, kind === 'success' && styles.noticeSuccess]}>
      <Text style={styles.noticeText}>{text}</Text>
    </View>
  );
}

function Header({ authenticated, onLogout }: { authenticated: boolean; onLogout: () => void }) {
  return (
    <View style={styles.header}>
      <View style={styles.brand}>
        <View style={styles.logo}><Text style={styles.logoText}>У</Text></View>
        <View>
          <Text style={styles.brandName}>Удалифон</Text>
          <Text style={styles.brandTag}>CPU background removal</Text>
        </View>
      </View>
      <View style={styles.headerActions}>
        <a className="nav-link" href="/docs" target="_blank" rel="noreferrer">Swagger API ↗</a>
        {authenticated && <Button secondary onPress={onLogout}>Выйти</Button>}
      </View>
    </View>
  );
}

function Welcome({ onAuthenticated }: { onAuthenticated: (key: string, reveal: boolean) => void }) {
  const [mode, setMode] = useState<'register' | 'key'>('register');
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [key, setKey] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const submit = async () => {
    setBusy(true);
    setError('');
    try {
      if (mode === 'register') {
        const result = await api.register(name, email);
        onAuthenticated(result.apiKey, true);
      } else {
        const clean = key.trim();
        await api.balance(clean);
        onAuthenticated(clean, false);
      }
    } catch (problem) {
      setError(messageOf(problem));
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={styles.welcomeGrid}>
      <View style={styles.hero}>
        <View style={styles.pill}><Text style={styles.pillText}>БЕЗ GPU · ПОЛНОСТЬЮ НА .NET</Text></View>
        <Text style={styles.heroTitle}>Чистый фон.{`\n`}Чистый процесс.</Text>
        <Text style={styles.heroText}>
          Загружайте изображения, удаляйте фон и управляйте API в одном кабинете.
          Новый аккаунт получает 1000 ₽ на старт.
        </Text>
        <View style={styles.heroPoints}>
          <Text style={styles.heroPoint}>✓ JPEG, PNG и WebP</Text>
          <Text style={styles.heroPoint}>✓ Прозрачный PNG на выходе</Text>
          <Text style={styles.heroPoint}>✓ От 0,20 ₽ за изображение</Text>
        </View>
      </View>

      <View style={[styles.card, styles.authCard]}>
        <View style={styles.tabs}>
          <Pressable onPress={() => setMode('register')} style={[styles.tab, mode === 'register' && styles.tabActive]}>
            <Text style={[styles.tabText, mode === 'register' && styles.tabTextActive]}>Новый аккаунт</Text>
          </Pressable>
          <Pressable onPress={() => setMode('key')} style={[styles.tab, mode === 'key' && styles.tabActive]}>
            <Text style={[styles.tabText, mode === 'key' && styles.tabTextActive]}>У меня есть ключ</Text>
          </Pressable>
        </View>
        <Text style={styles.cardTitle}>{mode === 'register' ? 'Начать бесплатно' : 'Войти по API-ключу'}</Text>
        <Text style={styles.muted}>
          {mode === 'register' ? 'Без карты. На балансе сразу 1000 ₽.' : 'Ключ хранится только в этом браузере.'}
        </Text>
        <View style={styles.formStack}>
          {mode === 'register' ? (
            <>
              <Field label="Имя или компания" value={name} onChange={setName} placeholder="ООО Ромашка" />
              <Field label="Email" value={email} onChange={setEmail} type="email" placeholder="hello@example.ru" />
            </>
          ) : (
            <Field label="API-ключ" value={key} onChange={setKey} type="password" placeholder="rb_live_..." />
          )}
          {error && <Notice text={error} />}
          <Button onPress={submit} disabled={busy || (mode === 'register' ? !name.trim() || !email.trim() : !key.trim())}>
            {busy ? 'Подключаем…' : mode === 'register' ? 'Создать аккаунт' : 'Открыть кабинет'}
          </Button>
        </View>
      </View>
    </View>
  );
}

function Processor({ apiKey, onProcessed }: { apiKey: string; onProcessed: () => void }) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [originalUrl, setOriginalUrl] = useState('');
  const [resultUrl, setResultUrl] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [resultMessage, setResultMessage] = useState('');

  useEffect(() => () => {
    if (originalUrl) URL.revokeObjectURL(originalUrl);
  }, [originalUrl]);
  useEffect(() => () => {
    if (resultUrl) URL.revokeObjectURL(resultUrl);
  }, [resultUrl]);

  const choose = (next: File | undefined) => {
    if (!next) return;
    setFile(next);
    setOriginalUrl(URL.createObjectURL(next));
    setResultUrl('');
    setError('');
    setResultMessage('');
  };

  const process = async () => {
    if (!file) return;
    setBusy(true);
    setError('');
    try {
      const result = await api.removeBackground(apiKey, file);
      if (resultUrl) URL.revokeObjectURL(resultUrl);
      setResultUrl(URL.createObjectURL(result.blob));
      setResultMessage(`Списано ${result.chargedRub ?? '—'} ₽ · баланс ${result.balanceRub ?? '—'} ₽`);
      onProcessed();
    } catch (problem) {
      setError(messageOf(problem));
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={[styles.card, styles.processorCard]}>
      <View style={styles.sectionHeading}>
        <View>
          <Text style={styles.eyebrow}>ОБРАБОТКА</Text>
          <Text style={styles.cardTitle}>Удалить фон</Text>
        </View>
        <Text style={styles.muted}>до 15 МБ</Text>
      </View>
      <input
        ref={inputRef}
        className="hidden-file"
        type="file"
        accept="image/jpeg,image/png,image/webp"
        onChange={(event) => choose(event.target.files?.[0])}
      />
      {!file ? (
        <Pressable
          onPress={() => inputRef.current?.click()}
          onDrop={(event: unknown) => {
            const dragEvent = event as DragEvent;
            dragEvent.preventDefault();
            choose(dragEvent.dataTransfer.files?.[0]);
          }}
          onDragOver={(event: unknown) => (event as DragEvent).preventDefault()}
          style={styles.dropzone}
        >
          <View style={styles.uploadIcon}><Text style={styles.uploadIconText}>↑</Text></View>
          <Text style={styles.dropTitle}>Перетащите изображение сюда</Text>
          <Text style={styles.muted}>или нажмите, чтобы выбрать файл</Text>
        </Pressable>
      ) : (
        <>
          <View style={styles.previewGrid}>
            <View style={styles.previewPane}>
              <Text style={styles.previewLabel}>ОРИГИНАЛ</Text>
              <img className="preview-image" src={originalUrl} alt="Оригинал" />
            </View>
            <View style={[styles.previewPane, styles.checker]}>
              <Text style={styles.previewLabel}>РЕЗУЛЬТАТ</Text>
              {busy ? (
                <View style={styles.previewEmpty}>
                  <ActivityIndicator color="#27d3a2" size="large" />
                  <Text style={styles.muted}>Обрабатываем на CPU…</Text>
                </View>
              ) : resultUrl ? (
                <img className="preview-image" src={resultUrl} alt="Без фона" />
              ) : (
                <View style={styles.previewEmpty}><Text style={styles.muted}>Здесь появится PNG</Text></View>
              )}
            </View>
          </View>
          <View style={styles.fileLine}>
            <View style={styles.fileMeta}>
              <Text style={styles.fileName} numberOfLines={1}>{file.name}</Text>
              <Text style={styles.muted}>{bytes(file.size)}</Text>
            </View>
            <Button secondary onPress={() => inputRef.current?.click()}>Заменить</Button>
          </View>
          {error && <Notice text={error} />}
          {resultMessage && <Notice text={resultMessage} kind="success" />}
          <View style={styles.actionRow}>
            <Button onPress={process} disabled={busy}>{busy ? 'Обрабатываем…' : 'Удалить фон'}</Button>
            {resultUrl && <a className="download-button" href={resultUrl} download="background-removed.png">Скачать PNG ↓</a>}
          </View>
        </>
      )}
    </View>
  );
}

function Dashboard({ apiKey, freshKey, onKeySeen, onUnauthorized }: { apiKey: string; freshKey: string; onKeySeen: () => void; onUnauthorized: () => void }) {
  const [balance, setBalance] = useState<Balance | null>(null);
  const [usage, setUsage] = useState<Usage | null>(null);
  const [keys, setKeys] = useState<ApiKeyView[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [keyName, setKeyName] = useState('');
  const [createdKey, setCreatedKey] = useState<ApiKeyCreated | null>(null);
  const [adminOpen, setAdminOpen] = useState(false);
  const [adminToken, setAdminToken] = useState('');
  const [topUpAccountId, setTopUpAccountId] = useState('');
  const [topUpAmount, setTopUpAmount] = useState('1000');
  const [topUpDescription, setTopUpDescription] = useState('Административное пополнение');
  const [notice, setNotice] = useState('');
  const [busyAction, setBusyAction] = useState('');

  const refresh = useCallback(async () => {
    try {
      const [nextBalance, nextUsage, nextKeys] = await Promise.all([
        api.balance(apiKey), api.usage(apiKey), api.keys(apiKey),
      ]);
      setBalance(nextBalance);
      setUsage(nextUsage);
      setKeys(nextKeys);
      setError('');
    } catch (problem) {
      if (problem instanceof ApiError && problem.status === 401) onUnauthorized();
      else setError(messageOf(problem));
    } finally {
      setLoading(false);
    }
  }, [apiKey, onUnauthorized]);

  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => {
    if (balance && !topUpAccountId) setTopUpAccountId(balance.accountId);
  }, [balance, topUpAccountId]);

  const createKey = async () => {
    setBusyAction('key');
    setNotice('');
    try {
      const result = await api.createKey(apiKey, keyName);
      setCreatedKey(result);
      setKeyName('');
      await refresh();
    } catch (problem) {
      setNotice(messageOf(problem));
    } finally {
      setBusyAction('');
    }
  };

  const revoke = async (id: string) => {
    if (!window.confirm('Отозвать этот API-ключ? Действие нельзя отменить.')) return;
    setBusyAction(id);
    try {
      await api.revokeKey(apiKey, id);
      await refresh();
    } catch (problem) {
      setNotice(messageOf(problem));
    } finally {
      setBusyAction('');
    }
  };

  const topUp = async () => {
    if (!topUpAccountId) return;
    setBusyAction('topup');
    setNotice('');
    try {
      const result = await api.topUp(topUpAccountId, adminToken, Number(topUpAmount), topUpDescription);
      setNotice(`Баланс пополнен на ${money(result.addedRub)}. Новый баланс: ${money(result.balanceRub)}.`);
      await refresh();
    } catch (problem) {
      setNotice(messageOf(problem));
    } finally {
      setBusyAction('');
    }
  };

  const activeKeys = useMemo(() => keys.filter((item) => !item.revokedAt).length, [keys]);

  if (loading) return <View style={styles.loader}><ActivityIndicator color="#27d3a2" size="large" /></View>;

  return (
    <View style={styles.dashboard}>
      {error && <Notice text={error} />}
      {freshKey && (
        <View style={styles.secretBox}>
          <Text style={styles.secretTitle}>Сохраните первый API-ключ — повторно сервер его не покажет</Text>
          <code>{freshKey}</code>
          <View style={styles.actionRow}>
            <button className="copy-link" onClick={() => navigator.clipboard.writeText(freshKey)}>Копировать ключ</button>
            <button className="copy-link" onClick={onKeySeen}>Я сохранил</button>
          </View>
        </View>
      )}
      <View style={styles.statsGrid}>
        <View style={[styles.statCard, styles.balanceCard]}>
          <Text style={styles.statLabel}>БАЛАНС</Text>
          <Text style={styles.statValue}>{money(balance?.balanceRub ?? 0)}</Text>
          <Text style={styles.statHint}>Аккаунт {balance?.accountId.slice(0, 8)}…</Text>
        </View>
        <View style={styles.statCard}>
          <Text style={styles.statLabel}>ОБРАБОТАНО В ЭТОМ МЕСЯЦЕ</Text>
          <Text style={styles.statValue}>{balance?.successfulImagesThisMonth ?? 0}</Text>
          <Text style={styles.statHint}>успешных изображений</Text>
        </View>
        <View style={styles.statCard}>
          <Text style={styles.statLabel}>СЛЕДУЮЩАЯ ОБРАБОТКА</Text>
          <Text style={styles.statValue}>{money(balance?.nextImagePriceRub ?? 0)}</Text>
          <Text style={styles.statHint}>спишется только при успехе</Text>
        </View>
        <View style={styles.statCard}>
          <Text style={styles.statLabel}>АКТИВНЫЕ КЛЮЧИ</Text>
          <Text style={styles.statValue}>{activeKeys}</Text>
          <Text style={styles.statHint}>из {keys.length} созданных</Text>
        </View>
      </View>

      <Processor apiKey={apiKey} onProcessed={refresh} />

      <View style={styles.twoColumns}>
        <View style={[styles.card, styles.columnCard]}>
          <View style={styles.sectionHeading}>
            <View><Text style={styles.eyebrow}>АКТИВНОСТЬ</Text><Text style={styles.cardTitle}>Последние запросы</Text></View>
            <Text style={styles.muted}>{usage?.successful ?? 0} успешно · {money(usage?.chargedRub ?? 0)}</Text>
          </View>
          <ScrollView style={styles.listScroll}>
            {!usage?.items.length && <Text style={styles.empty}>Пока ни одного запроса.</Text>}
            {usage?.items.map((item) => (
              <View key={item.id} style={styles.listItem}>
                <View style={[styles.statusDot, item.status !== 'success' && styles.statusError]} />
                <View style={styles.listContent}>
                  <Text style={styles.listTitle} numberOfLines={1}>{item.filename ?? 'Без имени'}</Text>
                  <Text style={styles.listMeta}>{date(item.createdAt)} · {item.durationMs} мс</Text>
                </View>
                <View style={styles.listAmount}>
                  <Text style={styles.listTitle}>{item.status === 'success' ? money(item.priceRub) : 'Ошибка'}</Text>
                  <Text style={styles.listMeta}>{item.status === 'success' ? bytes(item.outputBytes) : item.errorCode}</Text>
                </View>
              </View>
            ))}
          </ScrollView>
        </View>

        <View style={[styles.card, styles.columnCard]}>
          <Text style={styles.eyebrow}>ДОСТУП</Text>
          <Text style={styles.cardTitle}>API-ключи</Text>
          <View style={styles.inlineForm}>
            <div className="grow"><Field label="Название нового ключа" value={keyName} onChange={setKeyName} placeholder="Продакшен" /></div>
            <Button onPress={createKey} disabled={!keyName.trim() || busyAction === 'key'}>Создать</Button>
          </View>
          {createdKey && (
            <View style={styles.secretBox}>
              <Text style={styles.secretTitle}>Скопируйте ключ сейчас — повторно он не показывается</Text>
              <code>{createdKey.apiKey}</code>
              <button className="copy-link" onClick={() => navigator.clipboard.writeText(createdKey.apiKey)}>Копировать</button>
            </View>
          )}
          <ScrollView style={styles.listScroll}>
            {keys.map((item) => (
              <View key={item.id} style={styles.listItem}>
                <View style={[styles.keyIcon, item.revokedAt ? styles.keyIconOff : undefined]}><Text>⌁</Text></View>
                <View style={styles.listContent}>
                  <Text style={styles.listTitle}>{item.name}</Text>
                  <Text style={styles.listMeta}>{item.prefix}… · {item.revokedAt ? 'отозван' : `был использован ${date(item.lastUsedAt)}`}</Text>
                </View>
                {!item.revokedAt && <Button danger onPress={() => revoke(item.id)} disabled={busyAction === item.id}>Отозвать</Button>}
              </View>
            ))}
          </ScrollView>
        </View>
      </View>

      <View style={styles.card}>
        <Pressable style={styles.sectionHeading} onPress={() => setAdminOpen((value) => !value)}>
          <View><Text style={styles.eyebrow}>АДМИНИСТРИРОВАНИЕ</Text><Text style={styles.cardTitle}>Ручное пополнение</Text></View>
          <Text style={styles.expand}>{adminOpen ? 'Свернуть ↑' : 'Открыть ↓'}</Text>
        </Pressable>
        {adminOpen && (
          <View style={styles.adminForm}>
            <Field label="Admin-токен" value={adminToken} onChange={setAdminToken} type="password" placeholder="Не сохраняется в браузере" />
            <Field label="ID аккаунта" value={topUpAccountId} onChange={setTopUpAccountId} placeholder="UUID аккаунта" />
            <Field label="Сумма, ₽" value={topUpAmount} onChange={setTopUpAmount} type="number" />
            <Field label="Описание" value={topUpDescription} onChange={setTopUpDescription} />
            <Button onPress={topUp} disabled={!adminToken || !topUpAccountId || Number(topUpAmount) <= 0 || busyAction === 'topup'}>Пополнить баланс</Button>
          </View>
        )}
        {notice && <Notice text={notice} kind={notice.startsWith('Баланс') ? 'success' : 'error'} />}
      </View>

      <View style={styles.footer}>
        <Text style={styles.muted}>Удалифон · ASP.NET Core + ONNX Runtime CPU + React</Text>
        <a className="nav-link" href="/health/ready" target="_blank" rel="noreferrer">Статус API</a>
      </View>
    </View>
  );
}

export default function App() {
  const [apiKey, setApiKey] = useState(() => localStorage.getItem(STORAGE_KEY) ?? '');
  const [freshKey, setFreshKey] = useState('');

  const authenticate = (key: string, reveal: boolean) => {
    localStorage.setItem(STORAGE_KEY, key);
    setApiKey(key);
    setFreshKey(reveal ? key : '');
  };
  const logout = useCallback(() => {
    localStorage.removeItem(STORAGE_KEY);
    setApiKey('');
    setFreshKey('');
  }, []);

  return (
    <View style={styles.app}>
      <View style={styles.glowOne} />
      <View style={styles.glowTwo} />
      <View style={styles.container}>
        <Header authenticated={Boolean(apiKey)} onLogout={logout} />
        {apiKey ? (
          <Dashboard apiKey={apiKey} freshKey={freshKey} onKeySeen={() => setFreshKey('')} onUnauthorized={logout} />
        ) : (
          <Welcome onAuthenticated={authenticate} />
        )}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  app: { minHeight: '100vh', backgroundColor: '#07111f', color: '#edf5f4', position: 'relative', overflow: 'hidden' },
  container: { width: '100%', maxWidth: 1240, marginHorizontal: 'auto', paddingHorizontal: 24, zIndex: 1 },
  glowOne: { position: 'absolute', width: 600, height: 600, borderRadius: 999, backgroundColor: '#126c65', opacity: 0.18, filter: 'blur(100px)', top: -340, right: -180 },
  glowTwo: { position: 'absolute', width: 500, height: 500, borderRadius: 999, backgroundColor: '#1d4b93', opacity: 0.13, filter: 'blur(100px)', bottom: -300, left: -200 },
  header: { minHeight: 88, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 20, borderBottomWidth: 1, borderBottomColor: '#1b2a3c' },
  brand: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  logo: { width: 42, height: 42, borderRadius: 12, backgroundColor: '#27d3a2', alignItems: 'center', justifyContent: 'center', transform: 'rotate(-4deg)' },
  logoText: { color: '#06131c', fontWeight: '900', fontSize: 24 },
  brandName: { color: '#f5fbfa', fontSize: 19, fontWeight: '800', letterSpacing: -0.4 },
  brandTag: { color: '#708198', fontSize: 11, marginTop: 1 },
  headerActions: { flexDirection: 'row', alignItems: 'center', gap: 16 },
  welcomeGrid: { minHeight: 'calc(100vh - 88px)', flexDirection: 'row', alignItems: 'center', gap: 72, paddingVertical: 64 },
  hero: { flex: 1.1 },
  pill: { alignSelf: 'flex-start', borderWidth: 1, borderColor: '#27544f', backgroundColor: '#102b2b', paddingVertical: 8, paddingHorizontal: 12, borderRadius: 999, marginBottom: 24 },
  pillText: { color: '#74e1c1', fontWeight: '700', fontSize: 11, letterSpacing: 1.4 },
  heroTitle: { color: '#f5fbfa', fontSize: 68, lineHeight: 70, letterSpacing: -3.5, fontWeight: '800' },
  heroText: { color: '#9eb0c3', fontSize: 19, lineHeight: 30, maxWidth: 610, marginTop: 24 },
  heroPoints: { marginTop: 32, gap: 12 },
  heroPoint: { color: '#c7d4dd', fontSize: 15 },
  card: { backgroundColor: '#0e1b2b', borderWidth: 1, borderColor: '#1c3044', borderRadius: 22, padding: 24, boxShadow: '0 24px 80px rgba(0,0,0,.18)' },
  authCard: { flex: 0.8, maxWidth: 460, minWidth: 380, padding: 30 },
  tabs: { flexDirection: 'row', backgroundColor: '#081421', borderRadius: 12, padding: 4, marginBottom: 28 },
  tab: { flex: 1, borderRadius: 9, paddingVertical: 11, alignItems: 'center' },
  tabActive: { backgroundColor: '#1a2b3d' },
  tabText: { color: '#71849a', fontWeight: '650', fontSize: 13 },
  tabTextActive: { color: '#eef7f5' },
  cardTitle: { color: '#f1f7f7', fontWeight: '750', fontSize: 24, letterSpacing: -0.6 },
  muted: { color: '#8293a8', fontSize: 13, lineHeight: 19 },
  formStack: { gap: 16, marginTop: 24 },
  button: { minHeight: 43, borderRadius: 11, paddingVertical: 11, paddingHorizontal: 17, backgroundColor: '#27d3a2', alignItems: 'center', justifyContent: 'center' },
  buttonSecondary: { backgroundColor: '#142439', borderWidth: 1, borderColor: '#2a3d53' },
  buttonDanger: { backgroundColor: '#3b1c28' },
  buttonMuted: { opacity: 0.55 },
  buttonText: { color: '#06151b', fontWeight: '800', fontSize: 13 },
  buttonSecondaryText: { color: '#c4d2dc' },
  notice: { backgroundColor: '#421d28', borderWidth: 1, borderColor: '#733040', borderRadius: 10, padding: 12 },
  noticeSuccess: { backgroundColor: '#12362f', borderColor: '#245e50' },
  noticeText: { color: '#f1dfe3', fontSize: 13, lineHeight: 19 },
  loader: { minHeight: 500, alignItems: 'center', justifyContent: 'center' },
  dashboard: { gap: 22, paddingVertical: 32 },
  statsGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 16 },
  statCard: { flex: 1, minWidth: 220, backgroundColor: '#0d1a2a', borderWidth: 1, borderColor: '#1b3044', padding: 20, borderRadius: 17 },
  balanceCard: { backgroundColor: '#0f2929', borderColor: '#23564d' },
  statLabel: { color: '#73869c', fontSize: 10, fontWeight: '800', letterSpacing: 1.1 },
  statValue: { color: '#f3f9f8', fontSize: 29, fontWeight: '800', letterSpacing: -1, marginTop: 10 },
  statHint: { color: '#6f8398', fontSize: 11, marginTop: 6 },
  processorCard: { padding: 26 },
  sectionHeading: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', gap: 20, marginBottom: 20 },
  eyebrow: { color: '#27d3a2', fontWeight: '800', fontSize: 10, letterSpacing: 1.5, marginBottom: 7 },
  dropzone: { minHeight: 290, borderWidth: 1, borderStyle: 'dashed', borderColor: '#34506a', borderRadius: 17, alignItems: 'center', justifyContent: 'center', gap: 9, backgroundColor: '#091725' },
  uploadIcon: { width: 54, height: 54, borderRadius: 17, backgroundColor: '#173d3b', alignItems: 'center', justifyContent: 'center', marginBottom: 6 },
  uploadIconText: { color: '#45dbad', fontSize: 27 },
  dropTitle: { color: '#edf5f4', fontSize: 17, fontWeight: '700' },
  previewGrid: { flexDirection: 'row', gap: 14 },
  previewPane: { flex: 1, height: 350, borderRadius: 15, overflow: 'hidden', backgroundColor: '#07121f', borderWidth: 1, borderColor: '#1e3449', position: 'relative' },
  checker: { backgroundColor: '#dbe4e5' },
  previewLabel: { position: 'absolute', zIndex: 2, top: 12, left: 12, color: '#e8f1f1', backgroundColor: 'rgba(4,12,22,.72)', paddingHorizontal: 9, paddingVertical: 5, borderRadius: 7, fontSize: 9, fontWeight: '800', letterSpacing: 1 },
  previewEmpty: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 12 },
  fileLine: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', gap: 14, marginTop: 16 },
  fileMeta: { flex: 1 },
  fileName: { color: '#e8f1f1', fontSize: 14, fontWeight: '700' },
  actionRow: { flexDirection: 'row', alignItems: 'center', gap: 12, marginTop: 16 },
  twoColumns: { flexDirection: 'row', gap: 22, alignItems: 'stretch' },
  columnCard: { flex: 1, minWidth: 0 },
  listScroll: { maxHeight: 340 },
  empty: { color: '#718399', paddingVertical: 35, textAlign: 'center' },
  listItem: { flexDirection: 'row', alignItems: 'center', gap: 11, borderTopWidth: 1, borderTopColor: '#1a2b3d', paddingVertical: 13 },
  statusDot: { width: 8, height: 8, borderRadius: 9, backgroundColor: '#27d3a2' },
  statusError: { backgroundColor: '#e66f88' },
  listContent: { flex: 1, minWidth: 0 },
  listAmount: { alignItems: 'flex-end' },
  listTitle: { color: '#dfe9eb', fontSize: 13, fontWeight: '650' },
  listMeta: { color: '#718398', fontSize: 10, marginTop: 4 },
  inlineForm: { flexDirection: 'row', alignItems: 'flex-end', gap: 10, marginVertical: 18 },
  secretBox: { backgroundColor: '#112d2c', borderWidth: 1, borderColor: '#22594e', borderRadius: 11, padding: 13, gap: 8, marginBottom: 10 },
  secretTitle: { color: '#bce9dc', fontSize: 11 },
  keyIcon: { width: 34, height: 34, borderRadius: 9, backgroundColor: '#173c38', alignItems: 'center', justifyContent: 'center' },
  keyIconOff: { opacity: 0.35 },
  expand: { color: '#77dbc0', fontSize: 12 },
  adminForm: { display: 'grid', gridTemplateColumns: '1.7fr 1.5fr .7fr 1.5fr auto', gap: 12, alignItems: 'end' },
  footer: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', paddingVertical: 18 },
});
