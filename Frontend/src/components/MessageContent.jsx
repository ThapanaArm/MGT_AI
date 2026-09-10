import { useMemo, useState } from 'react';
import hljs from 'highlight.js/lib/common';
import ChartBlock from './ChartBlock';

/**
 * Renders an assistant message, turning fenced code blocks into real code blocks
 * with a language label, syntax highlighting and a copy button.
 *
 * Deliberately NOT a full Markdown renderer. Model output is untrusted input, and a
 * general Markdown-to-HTML pass would need sanitising to be safe. Splitting on fences and
 * letting React render every other character as text means no HTML from the model is ever
 * interpreted. The only innerHTML is highlight.js output, which escapes the code it is
 * given — that is the library's documented, safe usage.
 */

const FENCE = /```([\w+#-]*)\r?\n([\s\S]*?)```/g;

/** Splits the text into plain-text and code segments, preserving order. */
function parseSegments(text) {
  const segments = [];
  let lastIndex = 0;

  for (const match of text.matchAll(FENCE)) {
    if (match.index > lastIndex) {
      segments.push({ type: 'text', value: text.slice(lastIndex, match.index) });
    }

    segments.push({
      type: 'code',
      language: match[1]?.toLowerCase() || '',
      value: match[2].replace(/\s+$/, ''),
    });

    lastIndex = match.index + match[0].length;
  }

  if (lastIndex < text.length) {
    segments.push({ type: 'text', value: text.slice(lastIndex) });
  }

  return segments;
}

/** "| a | b |" (leading/trailing pipe optional) -> ["a", "b"] */
function splitRow(line) {
  let trimmed = line.trim();
  if (trimmed.startsWith('|')) trimmed = trimmed.slice(1);
  if (trimmed.endsWith('|')) trimmed = trimmed.slice(0, -1);
  return trimmed.split('|').map((cell) => cell.trim());
}

/** A GFM header-separator row: "---", ":---", "---:" or ":---:" per column. */
function isSeparatorRow(line) {
  if (!line.includes('|') && !line.includes('-')) return false;
  const cells = splitRow(line);
  return cells.length > 0 && cells.every((cell) => /^:?-{1,}:?$/.test(cell));
}

/**
 * Finds every Markdown table inside a plain-text segment and splits it into alternating
 * 'text' and 'table' sub-segments. A table needs a header row immediately followed by a
 * valid separator row (GFM's own rule for telling a table from a line that merely contains
 * a pipe) — everything else stays untouched plain text.
 */
function splitTables(text) {
  const lines = text.split('\n');
  const out = [];
  let buffer = [];
  let i = 0;

  const flushText = () => {
    if (buffer.length > 0) {
      out.push({ type: 'text', value: buffer.join('\n') });
      buffer = [];
    }
  };

  while (i < lines.length) {
    const header = lines[i];
    const separator = lines[i + 1];

    if (
      header?.includes('|') &&
      separator !== undefined &&
      isSeparatorRow(separator) &&
      splitRow(header).length === splitRow(separator).length
    ) {
      flushText();

      const columns = splitRow(header);
      const rows = [];
      let j = i + 2;

      while (j < lines.length && lines[j].includes('|') && lines[j].trim().length > 0) {
        rows.push(splitRow(lines[j]));
        j++;
      }

      out.push({ type: 'table', columns, rows });
      i = j;
    } else {
      buffer.push(header);
      i++;
    }
  }

  flushText();
  return out;
}

function MarkdownTable({ columns, rows }) {
  return (
    <div className="msg-table-wrap">
      <table className="msg-table">
        <thead>
          <tr>
            {columns.map((col, i) => (
              <th key={i}>{col}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, ri) => (
            <tr key={ri}>
              {columns.map((_, ci) => (
                <td key={ci}>{row[ci] ?? ''}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function CodeBlock({ language, value }) {
  const [copied, setCopied] = useState(false);

  const highlighted = useMemo(() => {
    try {
      // An unknown or missing tag falls back to auto-detection rather than failing.
      if (language && hljs.getLanguage(language)) {
        return hljs.highlight(value, { language, ignoreIllegals: true }).value;
      }
      return hljs.highlightAuto(value).value;
    } catch {
      return null;
    }
  }, [language, value]);

  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard access can be refused (insecure origin, permissions) — the code is
      // still selectable by hand, so this is not worth an error message.
    }
  }

  const lineCount = value.split('\n').length;

  return (
    <div className="code-block">
      <div className="code-head">
        <span className="code-lang">{language || 'code'}</span>
        <span className="code-lines">{lineCount} {lineCount === 1 ? 'line' : 'lines'}</span>
        <button type="button" className="code-copy" onClick={copy}>
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      <pre>
        {highlighted === null ? (
          <code>{value}</code>
        ) : (
          // Safe: highlight.js escapes the source it was handed.
          <code className="hljs" dangerouslySetInnerHTML={{ __html: highlighted }} />
        )}
      </pre>
    </div>
  );
}

export default function MessageContent({ text }) {
  const segments = useMemo(() => {
    // Code fences first (so a pipe inside a fenced code block is never mistaken for a table),
    // then every plain-text segment is scanned for Markdown tables.
    return parseSegments(text ?? '').flatMap((segment) =>
      segment.type === 'text' ? splitTables(segment.value) : [segment],
    );
  }, [text]);

  // Nothing but plain text: keep the original plain bubble so ordinary chat looks unchanged.
  if (!segments.some((s) => s.type === 'code' || s.type === 'table')) {
    return <>{text}</>;
  }

  return (
    <>
      {segments.map((segment, index) => {
        if (segment.type === 'table') {
          return <MarkdownTable key={index} columns={segment.columns} rows={segment.rows} />;
        }

        if (segment.type === 'code') {
          // A ```chart block carries data, not code — it is drawn rather than syntax-highlighted.
          return segment.language === 'chart' ? (
            <ChartBlock key={index} raw={segment.value} />
          ) : (
            <CodeBlock key={index} language={segment.language} value={segment.value} />
          );
        }

        return (
          <span key={index} className="msg-text">
            {segment.value.replace(/^\n+|\n+$/g, '')}
          </span>
        );
      })}
    </>
  );
}
