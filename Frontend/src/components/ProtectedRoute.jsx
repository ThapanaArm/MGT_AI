import { Link, Navigate, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

/**
 * Guards routes that require sign-in, and restricts by role when `roles` is given.
 * The backend enforces authorisation independently — this only keeps users away from
 * pages they cannot open.
 */
export default function ProtectedRoute({ children, roles }) {
  const { isAuthenticated, role } = useAuth();
  const location = useLocation();

  if (!isAuthenticated) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  if (roles && !roles.includes(role)) {
    return (
      <div className="page">
        <div className="card narrow">
          <div className="alert alert-danger">
            You do not have access to this page — your role is <strong>{role}</strong>, but this
            page is limited to {roles.join(' / ')}.
          </div>
          <Link to="/chat" className="btn">
            Back to chat
          </Link>
        </div>
      </div>
    );
  }

  return children;
}
