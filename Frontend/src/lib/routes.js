/** Role-restricted routes — must mirror the [Authorize(Roles = ...)] attributes on the backend. */
const ROUTE_ROLES = [
  { prefix: '/admin/', roles: ['Admin'] },
  // Covers /logs/chat, /logs/audit and /logs/cost
  { prefix: '/logs/', roles: ['Admin', 'Auditor'] },
];

export function canAccess(path, role) {
  const rule = ROUTE_ROLES.find((r) => path.startsWith(r.prefix));
  return !rule || rule.roles.includes(role);
}

/**
 * Where to land after a successful sign-in.
 * If the original path (the one that bounced the user to the login page) is not open to this
 * role, go to the chat page instead — nobody should sign in straight into a "no access" screen.
 */
export function landingPath(role, requestedPath) {
  if (!requestedPath || requestedPath === '/login' || requestedPath === '/') return '/chat';
  return canAccess(requestedPath, role) ? requestedPath : '/chat';
}
