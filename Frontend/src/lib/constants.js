/** Question-type labels — must stay in sync with QuestionClassifier on the backend. */
export const QUESTION_TYPES = [
  { value: 'WHAT', label: 'What' },
  { value: 'WHERE', label: 'Where' },
  { value: 'HOW', label: 'How' },
  { value: 'WHY', label: 'Why' },
  { value: 'WHEN', label: 'When' },
  { value: 'WHO', label: 'Who' },
  { value: 'HOWMUCH', label: 'How much' },
  { value: 'OTHER', label: 'Other' },
];

export const QUESTION_TYPE_LABELS = Object.fromEntries(
  QUESTION_TYPES.map((t) => [t.value, t.label]),
);

export const POLICY_FLAGS = [
  { value: 'Block', label: 'Block — not sent to the AI' },
  { value: 'Warn', label: 'Warn — sent, but the user is warned' },
  { value: 'Audit', label: 'Audit — flagged silently' },
];

export const AUDIT_CATEGORIES = [
  { value: 'AUTH', label: 'AUTH — sign in / sign out' },
  { value: 'CHAT', label: 'CHAT — chat activity' },
  { value: 'ADMIN', label: 'ADMIN — administrative actions' },
  { value: 'POLICY', label: 'POLICY — screening rules' },
];

export const USER_ROLES = [
  { value: 'Admin', label: 'Admin — read all logs + manage the system' },
  { value: 'Auditor', label: 'Auditor — read logs only' },
  { value: 'User', label: 'User — chat only' },
];

export const PAGE_SIZES = [25, 50, 100, 200];

export function questionTypeLabel(type) {
  return type ? (QUESTION_TYPE_LABELS[type] ?? type) : '—';
}

export function policyFlagClass(flag) {
  if (flag === 'Block') return 'badge badge-danger';
  if (flag === 'Warn') return 'badge badge-warn';
  if (flag === 'Audit') return 'badge badge-info';
  return 'badge';
}

// Force the Gregorian calendar so dates line up with the <input type="date"> filters,
// the values stored in SQL Server, and the exported CSV — auditors need to cross-reference.
const dateTimeFormat = new Intl.DateTimeFormat('en-GB', {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
});

export function formatDateTime(value) {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : dateTimeFormat.format(date);
}

const timeFormat = new Intl.DateTimeFormat('en-GB', {
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
});

export function formatTime(value) {
  if (!value) return '';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : timeFormat.format(date);
}

export function formatNumber(value) {
  return typeof value === 'number' ? value.toLocaleString('en-US') : '—';
}

/**
 * Cost in Thai baht. Per-message costs are usually well under one baht, so show enough
 * decimals that they do not round to 0.00, while large totals stay readable at 2 places.
 */
export function formatThb(value, { compact = false } = {}) {
  if (typeof value !== 'number') return '—';
  if (value === 0) return '0.00';

  const digits = compact || value >= 1 ? 2 : 4;
  return value.toLocaleString('en-US', {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
}

export function formatUsd(value) {
  if (typeof value !== 'number') return '—';
  if (value === 0) return '0.00';

  const digits = value >= 0.01 ? 4 : 6;
  return value.toLocaleString('en-US', {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
}

export function formatFileSize(bytes) {
  if (typeof bytes !== 'number' || bytes < 0) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** Icon per file kind — makes a list of attachments scannable at a glance. */
export function fileKindIcon(fileKind) {
  if (fileKind === 'Image') return '🖼️';
  if (fileKind === 'Pdf') return '📄';
  return '📝';
}

export const FILE_KIND_LABELS = {
  Image: 'Image',
  Pdf: 'PDF',
  Text: 'Text file',
};

/** yyyy-MM-dd for <input type="date">, using local time rather than UTC. */
export function toDateInput(date) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}
