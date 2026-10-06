import { NavLink, Outlet } from 'react-router-dom';

/** A sidebar section (Auditing / Administrator): a title, a tab bar, and the active tab's page below it. */
export default function SectionLayout({ title, tabs }) {
  return (
    <div className="section-shell">
      <div className="section-header">
        <h1>{title}</h1>
        <div className="tab-bar" role="tablist">
          {tabs.map((tab) => (
            <NavLink key={tab.to} to={tab.to} className="tab-bar-tab" role="tab">
              {tab.label}
            </NavLink>
          ))}
        </div>
      </div>
      <Outlet />
    </div>
  );
}
