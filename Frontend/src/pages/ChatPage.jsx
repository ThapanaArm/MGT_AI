import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useNavigate, useOutletContext, useSearchParams } from 'react-router-dom';
import { api, apiForm, download } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import MessageContent from '../components/MessageContent';
import {
  FILE_KIND_LABELS,
  fileKindIcon,
  formatFileSize,
  formatNumber,
  formatThb,
  formatTime,
  questionTypeLabel,
} from '../lib/constants';

/** คั่นสองบรรทัดเวลาแทรก Skill ต่อท้ายข้อความที่พิมพ์ไว้แล้ว — เขียนแยกตัวแปรเพื่อเลี่ยงปัญหา escape ของบรรทัดใหม่ */
const NEWLINE_GAP = String.fromCharCode(10, 10);

/** รูปแบบที่ส่งออกได้ — ตรงกับ ReportFormats ฝั่ง backend */
const EXPORT_FORMATS = [
  { format: 'xlsx', label: 'Excel', title: 'ตารางแยกชีต เปิดใน Excel แล้วกรอง/รวมยอดต่อได้' },
  { format: 'pdf', label: 'PDF', title: 'รายงานพร้อมส่ง อ่านได้ทุกเครื่อง' },
  { format: 'docx', label: 'Word', title: 'ไฟล์ที่แก้ไขต่อได้ก่อนส่ง' },
  { format: 'pptx', label: 'PowerPoint', title: 'สไลด์ หัวข้อละสไลด์ ตารางละสไลด์' },
];

const SUGGESTIONS = [
  {
    icon: '🔍', tone: 'brand', title: 'Search company knowledge',
    subtitle: 'What are the steps to create a Sales Order?',
    prompt: 'What are the steps to create a Sales Order?',
  },
  {
    icon: '📄', tone: 'info', title: 'Summarize documents',
    subtitle: 'Extract key points from your files',
    prompt: 'Please summarize the key points from the file I attach.',
  },
  {
    icon: '⚖️', tone: 'violet', title: 'Compare information',
    subtitle: 'INCOTERMS 2020 vs. 2010',
    prompt: 'How does INCOTERMS 2020 differ from the 2010 edition?',
  },
  {
    icon: '✏️', tone: 'warn', title: 'Draft content',
    subtitle: 'Write an email for work',
    prompt: 'Please draft a professional email for work — I will tell you the topic.',
  },
];

const AUDIT_LINKS = [
  { to: '/logs/chat', label: 'Search chat logs' },
  { to: '/logs/audit', label: 'System event log' },
  { to: '/logs/cost', label: 'Token cost report' },
];

const ADMIN_LINKS = [
  { to: '/admin/policy-rules', label: 'Question screening rules' },
  { to: '/admin/model-pricing', label: 'Model pricing / exchange rate' },
  { to: '/admin/users', label: 'User management' },
  { to: '/admin/data-sources', label: 'Data source registry' },
  { to: '/admin/data-source-access', label: 'Data source access' },
];

/** Guess the kind from the extension — only for the icon/label before upload; the server decides. */
function guessKind(fileName) {
  const ext = fileName.slice(fileName.lastIndexOf('.')).toLowerCase();
  if (['.png', '.jpg', '.jpeg', '.webp', '.gif'].includes(ext)) return 'Image';
  if (ext === '.pdf') return 'Pdf';
  if (['.xlsx', '.xls'].includes(ext)) return 'Spreadsheet';
  return 'Text';
}

export default function ChatPage() {
  const { isAdmin, canReadLogs } = useAuth();
  const navigate = useNavigate();
  const { historySlot, closeMobileSidebar } = useOutletContext() ?? {};
  const [showAdminMenu, setShowAdminMenu] = useState(false);
  const [sessions, setSessions] = useState([]);
  const [activeSessionId, setActiveSessionId] = useState(null);
  const [messages, setMessages] = useState([]);
  const [draft, setDraft] = useState('');
  const [pending, setPending] = useState([]);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState(null);
  const [notice, setNotice] = useState(null);
  const [aiStatus, setAiStatus] = useState(null);
  const [limits, setLimits] = useState(null);
  const [dragging, setDragging] = useState(false);
  const [models, setModels] = useState([]);
  const [model, setModel] = useState('');
  const [mode, setMode] = useState('Chat');
  const [exporting, setExporting] = useState(null);
  const [projects, setProjects] = useState([]);
  const [selectedProjectId, setSelectedProjectId] = useState(null);
  const [skills, setSkills] = useState([]);
  const [showSkills, setShowSkills] = useState(false);
  const [dataSourceOptions, setDataSourceOptions] = useState([]);
  const [selectedDataSourceId, setSelectedDataSourceId] = useState(null);
  const [dataSourceStatus, setDataSourceStatus] = useState(null);
  const [refreshingDataSource, setRefreshingDataSource] = useState(false);
  const [sessionSearch, setSessionSearch] = useState('');
  const [searchParams, setSearchParams] = useSearchParams();
  const [showSaveProject, setShowSaveProject] = useState(false);
  const [saveProjectName, setSaveProjectName] = useState('');
  const [saveProjectInstructions, setSaveProjectInstructions] = useState('');
  const [savingProject, setSavingProject] = useState(false);
  const [saveProjectError, setSaveProjectError] = useState(null);

  const bottomRef = useRef(null);
  const fileInputRef = useRef(null);
  // Track dragenter/dragleave depth, otherwise dragging over child elements makes the frame flicker.
  const dragDepth = useRef(0);

  const loadSessions = useCallback(async () => {
    try {
      setSessions(await api('/api/chat/sessions'));
    } catch (err) {
      setError(err.message);
    }
  }, []);

  useEffect(() => {
    api('/api/projects').then(setProjects).catch(() => setProjects([]));
    api('/api/skills').then(setSkills).catch(() => setSkills([]));
    api('/api/chat/data-sources').then(setDataSourceOptions).catch(() => setDataSourceOptions([]));
  }, []);

  // Reactive (not mount-only): the sidebar's "+ New chat" link changes ?new= even while already
  // on /chat, which does not remount this page — only a searchParams-watching effect catches that.
  useEffect(() => {
    const projectParam = searchParams.get('project');
    const isNew = searchParams.get('new');
    if (!projectParam && !isNew) return;

    setActiveSessionId(null);
    setSelectedDataSourceId(null);
    setSelectedProjectId(projectParam ? Number(projectParam) : null);
    setNotice(null);
    setError(null);
    setPending([]);
    if (isNew) setDraft('');
    setSearchParams({}, { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  useEffect(() => {
    loadSessions();
    api('/api/chat/status').then(setAiStatus).catch(() => setAiStatus(null));
    api('/api/chat/upload-limits').then(setLimits).catch(() => setLimits(null));

    api('/api/chat/models')
      .then((list) => {
        setModels(list);
        setModel((current) => current || list.find((m) => m.isDefault)?.name || list[0]?.name || '');
      })
      .catch(() => setModels([]));
  }, [loadSessions]);

  // A conversation stays on the model that already answered in it, so opening an old chat
  // shows the model it actually used rather than the picker's last value.
  useEffect(() => {
    const used = [...messages].reverse().find((m) => m.modelName)?.modelName;
    if (used) setModel(used);

    const lastMode = [...messages].reverse().find((m) => m.chatMode)?.chatMode;
    if (lastMode) setMode(lastMode);
  }, [messages]);

  useEffect(() => {
    if (!activeSessionId) {
      setMessages([]);
      setDataSourceStatus(null);
      return;
    }

    let cancelled = false;
    api(`/api/chat/sessions/${activeSessionId}/messages`)
      .then((data) => {
        if (!cancelled) setMessages(data);
      })
      .catch((err) => {
        if (!cancelled) setError(err.message);
      });

    api(`/api/chat/sessions/${activeSessionId}/data-source`)
      .then((data) => {
        if (!cancelled) setDataSourceStatus(data.sourceId ? data : null);
      })
      .catch(() => {
        if (!cancelled) setDataSourceStatus(null);
      });

    return () => {
      cancelled = true;
    };
  }, [activeSessionId]);

  async function refreshDataSource() {
    if (!activeSessionId || refreshingDataSource) return;

    setRefreshingDataSource(true);
    setError(null);

    try {
      setDataSourceStatus(await api(`/api/chat/sessions/${activeSessionId}/data-source/refresh`, { method: 'POST' }));
    } catch (err) {
      setError(err.message);
    } finally {
      setRefreshingDataSource(false);
    }
  }

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages, sending]);

  /** Check files against the server limits first, so nobody waits for an upload that gets rejected. */
  function addFiles(incoming) {
    if (!incoming.length) return;

    const accepted = [];
    const problems = [];

    for (const file of incoming) {
      const ext = file.name.slice(file.name.lastIndexOf('.')).toLowerCase();

      if (limits && !limits.allowedExtensions.includes(ext)) {
        problems.push(`"${file.name}" — ${ext || '(no extension)'} is not supported`);
        continue;
      }

      if (limits && file.size > limits.maxFileMb * 1024 * 1024) {
        problems.push(`"${file.name}" — larger than ${limits.maxFileMb} MB`);
        continue;
      }

      if (file.size === 0) {
        problems.push(`"${file.name}" — the file is empty`);
        continue;
      }

      accepted.push(file);
    }

    const merged = [...pending, ...accepted];

    if (limits && merged.length > limits.maxFilesPerMessage) {
      problems.push(`At most ${limits.maxFilesPerMessage} files per message`);
      merged.length = limits.maxFilesPerMessage;
    }

    const totalMb = merged.reduce((sum, f) => sum + f.size, 0) / 1024 / 1024;
    if (limits && totalMb > limits.maxTotalMbPerMessage) {
      setError(
        `Total attachment size must not exceed ${limits.maxTotalMbPerMessage} MB ` +
          `(currently ${totalMb.toFixed(1)} MB)`,
      );
      return;
    }

    setPending(merged);
    setError(problems.length ? problems.join(' · ') : null);
  }

  /** ดาวน์โหลดรายงานของคำตอบนั้น — backend สร้างไฟล์และบันทึก audit ให้ */
  async function exportReport(messageId, format) {
    const key = `${messageId}-${format}`;
    setExporting(key);
    setError(null);

    try {
      await download(`/api/chat/messages/${messageId}/export/${format}`, `report.${format}`);
    } catch (err) {
      setError(err.message);
    } finally {
      setExporting(null);
    }
  }

  function removePending(index) {
    setPending((prev) => prev.filter((_, i) => i !== index));
  }

  function handleDragEnter(event) {
    event.preventDefault();
    if (!event.dataTransfer?.types?.includes('Files')) return;
    dragDepth.current += 1;
    setDragging(true);
  }

  function handleDragLeave(event) {
    event.preventDefault();
    dragDepth.current = Math.max(0, dragDepth.current - 1);
    if (dragDepth.current === 0) setDragging(false);
  }

  function handleDrop(event) {
    event.preventDefault();
    dragDepth.current = 0;
    setDragging(false);
    addFiles([...(event.dataTransfer?.files ?? [])]);
  }

  /** Also accept images pasted from the clipboard (Ctrl+V after a screenshot or copy). */
  function handlePaste(event) {
    const files = [...(event.clipboardData?.files ?? [])];
    if (files.length) {
      event.preventDefault();
      addFiles(files);
    }
  }

  async function handleSend(event) {
    event?.preventDefault();

    const message = draft.trim();
    if ((!message && pending.length === 0) || sending) return;

    setError(null);
    setNotice(null);
    setSending(true);

    const filesToSend = pending;
    setDraft('');
    setPending([]);

    const pendingId = `pending-${Date.now()}`;
    setMessages((prev) => [
      ...prev,
      {
        messageId: pendingId,
        messageRole: 'user',
        content: message || `[Attached: ${filesToSend.map((f) => f.name).join(', ')}]`,
        createdAt: new Date().toISOString(),
        attachments: filesToSend.map((f, i) => ({
          attachmentId: `${pendingId}-${i}`,
          fileName: f.name,
          fileKind: guessKind(f.name),
          sizeBytes: f.size,
          policyScanned: false,
        })),
      },
    ]);

    try {
      let result;

      if (filesToSend.length > 0) {
        const form = new FormData();
        form.append('message', message);
        if (activeSessionId) form.append('sessionId', activeSessionId);
        else {
          if (selectedProjectId) form.append('projectId', selectedProjectId);
          if (selectedDataSourceId) form.append('dataSourceId', selectedDataSourceId);
        }
        if (model) form.append('model', model);
        if (mode) form.append('mode', mode);
        filesToSend.forEach((file) => form.append('files', file, file.name));
        result = await apiForm('/api/chat/messages', form);
      } else {
        result = await api('/api/chat/messages', {
          method: 'POST',
          body: {
            sessionId: activeSessionId, message, model: model || undefined, mode,
            projectId: activeSessionId ? undefined : selectedProjectId || undefined,
            dataSourceId: activeSessionId ? undefined : selectedDataSourceId || undefined,
          },
        });
      }

      setMessages((prev) => [
        ...prev.filter((m) => m.messageId !== pendingId),
        result.userMessage,
        ...(result.assistantMessage ? [result.assistantMessage] : []),
      ]);

      if (result.policyNotice) setNotice(result.policyNotice);
      if (!activeSessionId) setActiveSessionId(result.sessionId);
      await loadSessions();
    } catch (err) {
      setMessages((prev) => prev.filter((m) => m.messageId !== pendingId));
      setDraft(message);
      setPending(filesToSend);
      setError(err.message);
    } finally {
      setSending(false);
    }
  }

  async function handleDelete(sessionId, event) {
    event.stopPropagation();
    if (!window.confirm('Hide this conversation from your list? (Messages stay in the system for auditing.)')) {
      return;
    }

    try {
      await api(`/api/chat/sessions/${sessionId}`, { method: 'DELETE' });
      if (sessionId === activeSessionId) setActiveSessionId(null);
      await loadSessions();
    } catch (err) {
      setError(err.message);
    }
  }

  function openSaveProject() {
    setSaveProjectName(activeSession?.title ?? '');
    setSaveProjectInstructions('');
    setSaveProjectError(null);
    setShowSaveProject(true);
  }

  async function handleSaveAsProject(event) {
    event.preventDefault();
    if (!activeSessionId || savingProject) return;

    setSavingProject(true);
    setSaveProjectError(null);

    try {
      const project = await api('/api/projects', {
        method: 'POST',
        body: { name: saveProjectName, instructions: saveProjectInstructions || null, isShared: false },
      });
      await api(`/api/chat/sessions/${activeSessionId}/project`, {
        method: 'PUT',
        body: { projectId: project.projectId },
      });
      setShowSaveProject(false);
      setNotice(`Moved this conversation into the new project "${project.name}"`);
      await loadSessions();
    } catch (err) {
      setSaveProjectError(err.message);
    } finally {
      setSavingProject(false);
    }
  }

  async function openAttachment(attachment) {
    try {
      await download(`/api/chat/attachments/${attachment.attachmentId}`, attachment.fileName);
    } catch (err) {
      setError(err.message);
    }
  }

  function handleKeyDown(event) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      handleSend();
    }
  }

  const totalPendingMb = pending.reduce((sum, f) => sum + f.size, 0) / 1024 / 1024;
  const selectedModel = models.find((m) => m.name === model);
  const activeSession = sessions.find((s) => s.sessionId === activeSessionId);

  const filteredSessions = sessionSearch.trim()
    ? sessions.filter((s) => s.title.toLowerCase().includes(sessionSearch.trim().toLowerCase()))
    : sessions;

  // Group the picker by vendor. With two providers a flat list makes it hard to see which
  // account a model bills to — and that is the thing a user needs to know before sending.
  const modelGroups = models.reduce((groups, m) => {
    const key = m.provider || 'Other';
    (groups[key] ||= []).push(m);
    return groups;
  }, {});

  return (
    <div className="chat-layout">
      {historySlot && createPortal(
      <div className="chat-sessions">
        <div className="chat-sessions-search">
          <span className="chat-sessions-search-icon" aria-hidden="true">🔎</span>
          <input
            type="search"
            placeholder="Search conversations"
            value={sessionSearch}
            onChange={(e) => setSessionSearch(e.target.value)}
          />
        </div>

        <div className="chat-sessions-label">Recent</div>

        <div className="chat-session-list">
          {sessions.length === 0 && <div className="empty">No conversations yet</div>}
          {sessions.length > 0 && filteredSessions.length === 0 && (
            <div className="empty">No conversations match "{sessionSearch}"</div>
          )}

          {filteredSessions.map((session) => (
            <div
              key={session.sessionId}
              className={`chat-session${session.sessionId === activeSessionId ? ' active' : ''}`}
              onClick={() => {
                setActiveSessionId(session.sessionId);
                closeMobileSidebar?.();
              }}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => {
                if (e.key !== 'Enter') return;
                setActiveSessionId(session.sessionId);
                closeMobileSidebar?.();
              }}
            >
              <span className="chat-session-icon" aria-hidden="true">💬</span>
              <div className="chat-session-body">
                <div className="chat-session-title">{session.title}</div>
                <div className="chat-session-meta">
                  {[
                    session.projectName,
                    session.dataSourceName && `🔌 ${session.dataSourceName}`,
                    `${session.messageCount} msg`,
                    formatTime(session.updatedAt),
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </div>
              </div>
              <button
                type="button"
                className="chat-session-delete"
                title="Hide conversation"
                onClick={(e) => handleDelete(session.sessionId, e)}
              >
                ×
              </button>
            </div>
          ))}
        </div>
      </div>,
      historySlot,
      )}

      <div
        className={`chat-main${dragging ? ' dragging' : ''}`}
        onDragEnter={handleDragEnter}
        onDragOver={(e) => e.preventDefault()}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
      >
        <div className="chat-topbar">
          <div className="chat-topbar-title">{activeSession?.title ?? 'New chat'}</div>

          {/* Read-only here — the composer below is where this is actually picked, so there is
              only ever one interactive control for it (see chat-composer-controls). */}
          {!activeSessionId ? (
            <span className="chat-source-pill">
              <span aria-hidden="true">🗄️</span> Source:{' '}
              {selectedDataSourceId
                ? dataSourceOptions.find((s) => s.sourceId === selectedDataSourceId)?.sourceName
                : 'none'}
            </span>
          ) : dataSourceStatus ? (
            <span className="chat-source-pill" title={dataSourceStatus.message}>
              <span aria-hidden="true">🗄️</span> Source: {dataSourceStatus.sourceName}
            </span>
          ) : (
            <span />
          )}

          {activeSessionId && !activeSession?.projectId && (
            <button type="button" className="btn btn-secondary btn-sm" onClick={openSaveProject}>
              📁 Save as Project
            </button>
          )}

          {isAdmin || canReadLogs ? (
            <div className="chat-topbar-admin">
              <button
                type="button"
                className="chat-topbar-gear"
                title={isAdmin ? 'Administrator settings' : 'Auditing'}
                onClick={() => setShowAdminMenu((v) => !v)}
                aria-expanded={showAdminMenu}
              >
                ⚙️
              </button>
              {showAdminMenu && (
                <div className="chat-topbar-admin-menu">
                  {canReadLogs && (
                    <>
                      <div className="chat-topbar-admin-menu-label">Auditing</div>
                      {AUDIT_LINKS.map((link) => (
                        <button
                          key={link.to}
                          type="button"
                          onClick={() => {
                            setShowAdminMenu(false);
                            navigate(link.to);
                          }}
                        >
                          {link.label}
                        </button>
                      ))}
                    </>
                  )}
                  {isAdmin && (
                    <>
                      <div className="chat-topbar-admin-menu-label">Administrator</div>
                      {ADMIN_LINKS.map((link) => (
                        <button
                          key={link.to}
                          type="button"
                          onClick={() => {
                            setShowAdminMenu(false);
                            navigate(link.to);
                          }}
                        >
                          {link.label}
                        </button>
                      ))}
                    </>
                  )}
                </div>
              )}
            </div>
          ) : (
            <button
              type="button"
              className="chat-topbar-help"
              title="Attach files, pick a Project or Data source before your first message, choose a model, and export any answer as a report."
            >
              ?
            </button>
          )}
        </div>

        {dragging && (
          <div className="drop-overlay">
            <div className="drop-overlay-inner">
              <div className="drop-icon">📎</div>
              <strong>Drop files here for the AI to analyse</strong>
              {limits && (
                <div className="faint" style={{ marginTop: 6 }}>
                  {limits.allowedExtensions.join(' ')} · up to {limits.maxFileMb} MB per file ·
                  {' '}max {limits.maxFilesPerMessage} files
                </div>
              )}
            </div>
          </div>
        )}

        <div className="chat-messages">
          {aiStatus && !aiStatus.aiReady && (
            <div className="alert alert-warn">
              {aiStatus.message} — logging and question screening still work as usual, but there
              will be no AI answers until an API key is configured.
            </div>
          )}

          {error && <div className="alert alert-danger">{error}</div>}
          {notice && <div className="alert alert-warn">{notice}</div>}

          {dataSourceStatus && (
            <div className={`alert ${dataSourceStatus.success ? 'alert-ok' : 'alert-warn'}`}>
              <strong>🔌 {dataSourceStatus.sourceName}</strong> — {dataSourceStatus.message}
              {dataSourceStatus.success && dataSourceStatus.charCount > 0 && (
                <> ({dataSourceStatus.charCount.toLocaleString()} characters{dataSourceStatus.truncated ? ', truncated' : ''})</>
              )}
              {' · '}fetched {formatTime(dataSourceStatus.fetchedAt)}
              {' · '}
              <button
                type="button"
                className="btn-link"
                onClick={refreshDataSource}
                disabled={refreshingDataSource}
              >
                {refreshingDataSource ? 'Refreshing…' : 'Refresh'}
              </button>
            </div>
          )}

          {messages.length === 0 && !sending && (
            <div className="chat-welcome">
              <div className="chat-welcome-icon" aria-hidden="true">✨</div>
              <h2>How can I help you today?</h2>
              <p>Find answers, summarize documents, and simplify your work.</p>

              <div className="chat-suggestions">
                {SUGGESTIONS.map((s) => (
                  <button
                    key={s.title}
                    type="button"
                    className="chat-suggestion-card"
                    onClick={() => setDraft(s.prompt)}
                  >
                    <span className={`chat-suggestion-icon tone-${s.tone}`} aria-hidden="true">{s.icon}</span>
                    <span className="chat-suggestion-text">
                      <strong>{s.title}</strong>
                      <span className="faint">{s.subtitle}</span>
                    </span>
                    <span className="chat-suggestion-arrow" aria-hidden="true">›</span>
                  </button>
                ))}
              </div>

              <p className="chat-welcome-footnote">
                Drag files onto this page to have the AI analyse them. Every question and
                attachment is logged with the sender and timestamp, and screened against company
                data policy before it reaches the AI.
              </p>
            </div>
          )}

          {messages.map((message) => (
            <div
              key={message.messageId}
              className={`msg ${message.messageRole}${message.isBlocked ? ' blocked' : ''}`}
            >
              {message.attachments?.length > 0 && (
                <div className="msg-files">
                  {message.attachments.map((file) => (
                    <button
                      key={file.attachmentId}
                      type="button"
                      className="file-chip"
                      title={
                        typeof file.attachmentId === 'number'
                          ? `${FILE_KIND_LABELS[file.fileKind] ?? file.fileKind} — click to download`
                          : 'Uploading…'
                      }
                      disabled={typeof file.attachmentId !== 'number'}
                      onClick={() => openAttachment(file)}
                    >
                      <span>{fileKindIcon(file.fileKind)}</span>
                      <span className="file-chip-name">{file.fileName}</span>
                      <span className="faint">{formatFileSize(file.sizeBytes)}</span>
                    </button>
                  ))}
                </div>
              )}

              <div className="msg-bubble">
                {message.messageRole === 'assistant' ? (
                  <MessageContent text={message.content} />
                ) : (
                  message.content
                )}
              </div>

              <div className="msg-meta">
                <span>{formatTime(message.createdAt)}</span>

                {message.messageRole === 'user' && message.questionType && (
                  <span className="badge badge-brand">{questionTypeLabel(message.questionType)}</span>
                )}

                {message.isBlocked && <span className="badge badge-danger">Blocked — not sent to AI</span>}

                {!message.isBlocked && message.policyFlag && (
                  <span className="badge badge-warn">
                    {message.policyFlag}: {message.policyRuleName}
                  </span>
                )}

                {message.chatMode === 'Code' && (
                  <span className="badge badge-info">Code</span>
                )}

                {message.messageRole === 'assistant' && !String(message.messageId).startsWith('pending')
                  && !message.isBlocked && (
                  <span className="export-row">
                    Export:
                    {EXPORT_FORMATS.map((f) => (
                      <button
                        key={f.format}
                        type="button"
                        className="btn-link"
                        disabled={exporting === `${message.messageId}-${f.format}`}
                        onClick={() => exportReport(message.messageId, f.format)}
                        title={f.title}
                      >
                        {exporting === `${message.messageId}-${f.format}` ? '…' : f.label}
                      </button>
                    ))}
                  </span>
                )}

                {message.messageRole === 'assistant' && message.modelName && (
                  <span className="model-badge">{message.modelName}</span>
                )}

                {message.messageRole === 'assistant' && message.outputTokens != null && (
                  <span className="faint">
                    tokens {formatNumber(message.inputTokens)}/{formatNumber(message.outputTokens)}
                    {message.cacheReadTokens > 0 && ` (cache ${formatNumber(message.cacheReadTokens)})`}
                    {message.totalCostThb != null && ` · ${formatThb(message.totalCostThb)} THB`} ·{' '}
                    {formatNumber(message.latencyMs)} ms
                  </span>
                )}
              </div>
            </div>
          ))}

          {sending && (
            <div className="msg assistant">
              <div className="msg-bubble">
                <span className="typing">
                  <span />
                  <span />
                  <span />
                </span>
              </div>
            </div>
          )}

          <div ref={bottomRef} />
        </div>

        <form className="chat-composer" onSubmit={handleSend}>
          {models.length > 0 && (
            <div className="model-bar">
              <span className="mode-toggle">
                {['Chat', 'Code'].map((m) => (
                  <button
                    key={m}
                    type="button"
                    className={mode === m ? 'active' : undefined}
                    onClick={() => setMode(m)}
                    disabled={sending}
                    title={m === 'Code'
                      ? 'Programming assistant — complete runnable code in highlighted blocks'
                      : 'General assistant — short prose answers'}
                  >
                    {m}
                  </button>
                ))}
              </span>

              {!activeSessionId && projects.length > 0 && (
                <>
                  <label htmlFor="project" style={{ margin: 0 }}>
                    Project
                  </label>
                  <select
                    id="project"
                    value={selectedProjectId ?? ''}
                    onChange={(e) => setSelectedProjectId(e.target.value ? Number(e.target.value) : null)}
                    disabled={sending}
                  >
                    <option value="">No project</option>
                    {projects.map((p) => (
                      <option key={p.projectId} value={p.projectId}>
                        {p.name}{p.ownerUserId !== undefined && !p.canEdit ? ' (shared)' : ''}
                      </option>
                    ))}
                  </select>
                </>
              )}

              <label htmlFor="model" style={{ margin: 0 }}>
                Model
              </label>
              <select
                id="model"
                className="model-select"
                value={model}
                onChange={(e) => setModel(e.target.value)}
                disabled={sending}
              >
                {Object.entries(modelGroups).map(([provider, list]) => (
                  <optgroup key={provider} label={provider}>
                    {list.map((m) => (
                      <option key={m.name} value={m.name}>
                        {m.name}
                        {m.isDefault ? ' (default)' : ''}
                        {m.providerReady === false ? ' — no API key' : ''}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </select>

              {selectedModel && (
                <span className="model-cost">
                  ≈ {formatThb(selectedModel.sampleCostThb)} THB per typical question · $
                  {selectedModel.inputUsdPerMTok}/{selectedModel.outputUsdPerMTok} per 1M tokens in/out
                </span>
              )}

              {selectedModel?.providerReady === false && (
                <span className="badge badge-danger">
                  {selectedModel.provider} has no API key configured — pick another provider or ask
                  an administrator to add it
                </span>
              )}

              {messages.length > 0 && (
                <span className="faint">Switching model or mode applies to your next message.</span>
              )}
            </div>
          )}

          {pending.length > 0 && (
            <div className="pending-files">
              {pending.map((file, index) => (
                <span key={`${file.name}-${index}`} className="file-chip">
                  <span>{fileKindIcon(guessKind(file.name))}</span>
                  <span className="file-chip-name">{file.name}</span>
                  <span className="faint">{formatFileSize(file.size)}</span>
                  <button
                    type="button"
                    className="file-chip-remove"
                    title="Remove this file"
                    onClick={() => removePending(index)}
                  >
                    ×
                  </button>
                </span>
              ))}
              <span className="faint" style={{ fontSize: 12, alignSelf: 'center' }}>
                {pending.length} file(s) · {totalPendingMb.toFixed(1)} MB
              </span>
            </div>
          )}

          <div className="chat-composer-row">
            <input
              ref={fileInputRef}
              type="file"
              multiple
              hidden
              accept={limits?.allowedExtensions.join(',')}
              onChange={(e) => {
                addFiles([...e.target.files]);
                e.target.value = '';
              }}
            />

            <div className="chat-composer-card">
              <textarea
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={handleKeyDown}
                onPaste={handlePaste}
                placeholder={mode === 'Code'
                  ? 'Describe what to build, paste code to review, or drop a file…'
                  : 'Ask a question or attach a file to analyze…'}
                rows={1}
                disabled={sending}
              />

              <div className="chat-composer-controls">
                <button
                  type="button"
                  className="composer-pill"
                  title="Attach files"
                  onClick={() => fileInputRef.current?.click()}
                  disabled={sending}
                >
                  📎 <span className="composer-pill-label">Attach files</span>
                </button>

                {dataSourceOptions.length > 0 && (
                  !activeSessionId ? (
                    <label
                      className="composer-pill"
                      title="Pull real data from a granted Data Source into this conversation"
                    >
                      <span aria-hidden="true">🗄️</span>
                      <select
                        value={selectedDataSourceId ?? ''}
                        onChange={(e) => setSelectedDataSourceId(e.target.value ? Number(e.target.value) : null)}
                        disabled={sending}
                      >
                        <option value="">Company data: none</option>
                        {dataSourceOptions.map((s) => (
                          <option key={s.sourceId} value={s.sourceId}>{s.sourceName}</option>
                        ))}
                      </select>
                    </label>
                  ) : dataSourceStatus ? (
                    <span className="composer-pill" title={dataSourceStatus.message}>
                      <span aria-hidden="true">🗄️</span> {dataSourceStatus.sourceName}
                    </span>
                  ) : null
                )}

                {skills.length > 0 && (
                  <div className="skills-picker">
                    <button
                      type="button"
                      className="composer-pill"
                      title="Insert a prompt from your library"
                      onClick={() => setShowSkills((v) => !v)}
                      disabled={sending}
                    >
                      ⚡ <span className="composer-pill-label">Prompt library</span>
                    </button>
                    {showSkills && (
                      <div className="skills-popover">
                        {skills.map((s) => (
                          <button
                            key={s.skillId}
                            type="button"
                            className="skills-popover-item"
                            title={s.body}
                            onClick={() => {
                              setDraft((prev) => (prev.trim() ? [prev, s.body].join(NEWLINE_GAP) : s.body));
                              setShowSkills(false);
                            }}
                          >
                            {s.name}
                            {!s.canEdit && <span className="faint"> (shared)</span>}
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                )}
              </div>
            </div>

            <button
              type="submit"
              className="btn chat-send-btn"
              title="Send"
              disabled={sending || (!draft.trim() && pending.length === 0)}
            >
              {sending ? '…' : '➤'}
            </button>
          </div>

          <div className="chat-hint">
            <span
              className="chat-hint-info"
              aria-hidden="true"
              title={
                'Never type or attach passwords, national ID numbers, or company secrets — messages and ' +
                'text files that match a rule are blocked and logged.' +
                (limits ? ` Supported: ${limits.allowedExtensions.join(' ')}` : '')
              }
            >
              ⓘ
            </span>
            Conversations are logged according to company policy
          </div>
        </form>

        <div className="chat-footnote">AI can make mistakes. Please verify the sources.</div>
      </div>

      {showSaveProject && (
        <div className="modal-backdrop" onClick={() => !savingProject && setShowSaveProject(false)}>
          <form className="modal-card" onClick={(e) => e.stopPropagation()} onSubmit={handleSaveAsProject}>
            <div className="card-title">Save this chat as a Project</div>
            <p className="faint">
              Creates a new project and moves this entire conversation into it — its history stays
              intact, and any further messages in it will use the project's instructions/files.
            </p>

            {saveProjectError && <div className="alert alert-danger">{saveProjectError}</div>}

            <div className="field field-wide">
              <label htmlFor="saveProjectName">Project name</label>
              <input
                id="saveProjectName"
                value={saveProjectName}
                onChange={(e) => setSaveProjectName(e.target.value)}
                maxLength={150}
                required
                autoFocus
              />
            </div>

            <div className="field field-wide">
              <label htmlFor="saveProjectInstructions">Instructions (optional)</label>
              <textarea
                id="saveProjectInstructions"
                rows={4}
                value={saveProjectInstructions}
                onChange={(e) => setSaveProjectInstructions(e.target.value)}
                placeholder="How should the AI treat future messages in this project?"
              />
            </div>

            <div className="field field-wide row-actions">
              <button type="submit" className="btn btn-primary" disabled={savingProject || !saveProjectName.trim()}>
                {savingProject ? 'Saving…' : 'Create project & move chat'}
              </button>
              <button
                type="button"
                className="btn btn-secondary"
                disabled={savingProject}
                onClick={() => setShowSaveProject(false)}
              >
                Cancel
              </button>
            </div>
          </form>
        </div>
      )}
    </div>
  );
}
