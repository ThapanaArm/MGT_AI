import { useTheme } from '../theme/ThemeContext';

/** Light / dark switch — a sliding toggle with a sun and a moon. */
export default function ThemeToggle() {
  const { isDark, toggleTheme } = useTheme();
  const label = isDark ? 'Switch to light mode' : 'Switch to dark mode';

  return (
    <button
      type="button"
      role="switch"
      aria-checked={isDark}
      aria-label={label}
      title={label}
      className={`theme-toggle${isDark ? ' is-dark' : ''}`}
      onClick={toggleTheme}
    >
      <span className="theme-toggle-icon" aria-hidden="true">☀️</span>
      <span className="theme-toggle-icon" aria-hidden="true">🌙</span>
      <span className="theme-toggle-knob" aria-hidden="true" />
    </button>
  );
}
