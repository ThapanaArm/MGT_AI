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
  const segments = useMemo(() => parseSegments(text ?? ''), [text]);

  // No fences: keep the original plain bubble so ordinary chat looks unchanged.
  if (!segments.some((s) => s.type === 'code')) {
    return <>{text}</>;
  }

  return (
    <>
      {segments.map((segment, index) =>
        segment.type === 'code' ? (
          // A ```chart block carries data, not code — it is drawn rather than syntax-highlighted.
          segment.language === 'chart' ? (
            <ChartBlock key={index} raw={segment.value} />
          ) : (
            <CodeBlock key={index} language={segment.language} value={segment.value} />
          )
        ) : (
          <span key={index} className="msg-text">
            {segment.value.replace(/^\n+|\n+$/g, '')}
          </span>
        ),
      )}
    </>
  );
}
