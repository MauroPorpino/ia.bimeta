// Tipos do painel de administração da Plataforma BImeta (JSON camelCase da API /platform/*).

export type CompanyStatus = 'Active' | 'Suspended' | 'Provisioning';

export interface CompanyView {
  tenantId: string;
  slug: string;
  name: string;
  status: CompanyStatus;
  legalName: string | null;
  document: string | null;
  contactName: string | null;
  contactEmail: string | null;
  createdAt: string;
  updatedAt: string;
  suspendedAt: string | null;
}

export interface SeatsView {
  limit: number | null;
  registrationEnabled: boolean;
  isExplicit: boolean;
  active: number;
  pendingReserved: number;
  pendingExpired: number;
  inactive: number;
  used: number;
  available: number | null;
  excess: number;
}

export interface CompanyListItem {
  company: CompanyView;
  seats: SeatsView;
}

export interface Pagina<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface Overview {
  totalCompanies: number;
  activeCompanies: number;
  suspendedCompanies: number;
  provisioningCompanies: number;
  seats: { contracted: number; active: number; reserved: number; available: number; companiesWithLimit: number };
  companiesOverLimit: number;
  companiesRegistrationDisabled: number;
  legacy: { companies: number; activeUsers: number };
  generatedAt: string;
}

export interface InitialAdministrator {
  email: string;
  displayName: string | null;
  status: 'Pending' | 'Expired' | 'Active' | string;
  hasLiveToken: boolean;
  createdAt: string;
}

export interface StatusHistoryEntry {
  changedAt: string;
  fromStatus: string;
  toStatus: string;
  reason: string | null;
  actorUserId: string | null;
}

export interface CompanyDetail {
  company: CompanyView;
  seats: SeatsView;
  initialAdministrator: InitialAdministrator | null;
  statusHistory: StatusHistoryEntry[];
}

export interface FirstAccess {
  email: string;
  token: string;
  expiresAt: string;
  reissued: boolean;
}

export interface CreateCompanyRequest {
  slug: string;
  name: string;
  legalName: string;
  document: string;
  contactName: string;
  contactEmail: string;
  maxUsers: number;
  registrationEnabled: boolean;
  adminEmail: string;
  adminName: string;
}

export interface CreateCompanyResult {
  detail: CompanyDetail;
  firstAccess: FirstAccess;
  created: boolean;
}

export interface UpdateCompanyRequest {
  name: string;
  legalName: string;
  document: string;
  contactName: string;
  contactEmail: string;
}

export interface StatusChangeRequest {
  reason: string;
  confirm: string;
}

export type OperatorStatus = 'Active' | 'Pending' | 'Expired' | 'Inactive' | 'Cancelled';
export type OperatorRole = 'PlatformAdministrator' | 'PlatformCommercial' | 'PlatformViewer';

export interface OperatorItem {
  id: string;
  email: string;
  displayName: string;
  status: OperatorStatus;
  role: string;
  hasLiveFirstAccessToken: boolean;
  createdAt: string;
}

export interface OperatorPermission {
  code: string;
  group: string;
  label: string;
  fromRole: boolean;
  override: 'Grant' | 'Deny' | null;
  effective: boolean;
}

export interface OperatorDetail {
  operator: OperatorItem;
  permissions: OperatorPermission[];
}

export interface OperatorToken {
  operator: OperatorItem;
  token: string;
  expiresAt: string;
  kind: 'first-access' | 'access-reset';
}

export interface RoleInfo {
  role: string;
  label: string;
  permissions: string[];
}

export interface AuditItem {
  id: string;
  occurredAt: string;
  actorUserId: string | null;
  actorOrigin: string | null;
  operation: string;
  resourceType: string | null;
  resourceId: string | null;
  outcome: 'Success' | 'Denied' | 'Failed' | string;
}

export interface AuditFilters {
  operation: string;
  resource: string;
  outcome: string;
  from: string;
  to: string;
}

export interface PlatformSession {
  token: string;
  expiresAt: string;
}

export interface PlatformMe {
  id: string;
  email: string;
  displayName: string;
  grants: Array<{ organizationId: string | null; role: string }>;
  preferredLocale: string | null;
  permissions: Array<{ permission: string; organizationId: string | null }>;
}
