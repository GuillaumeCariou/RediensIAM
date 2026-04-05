import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { CheckCircle2, XCircle, Mail, Building2, ChevronRight, FolderKanban } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { Button } from '@/components/ui/button';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '@/components/ui/table';
import { getEmailOverview } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface GlobalSmtp {
  configured: boolean;
  host?: string;
  port?: number;
  start_tls?: boolean;
  from_address?: string;
  from_name?: string;
}

interface ProjectOverride {
  id: string;
  name: string;
  email_from_name: string;
}

interface OrgRow {
  id: string;
  name: string;
  slug: string;
  smtp_configured: boolean;
  smtp_host?: string;
  smtp_port?: number;
  smtp_from_address?: string;
  smtp_from_name?: string;
  smtp_updated_at?: string;
  project_overrides: ProjectOverride[];
}

interface Overview {
  global_smtp: GlobalSmtp;
  orgs: OrgRow[];
}

function StatusDot({ ok, label }: Readonly<{ ok: boolean; label: string }>) {
  return (
    <span className="flex items-center gap-1.5">
      {ok
        ? <CheckCircle2 className="h-4 w-4 text-green-500 shrink-0" />
        : <XCircle      className="h-4 w-4 text-muted-foreground shrink-0" />}
      <span className={ok ? '' : 'text-muted-foreground'}>{label}</span>
    </span>
  );
}

export default function SystemEmail() {
  const navigate = useNavigate();
  const [data, setData]       = useState<Overview | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  useEffect(() => {
    getEmailOverview()
      .then(setData)
      .catch(e => setError(e?.message ?? 'Failed to load email overview'))
      .finally(() => setLoading(false));
  }, []);

  const customCount   = data?.orgs.filter(o => o.smtp_configured).length ?? 0;
  const totalOrgs     = data?.orgs.length ?? 0;
  const overrideCount = data?.orgs.reduce((n, o) => n + o.project_overrides.length, 0) ?? 0;

  if (loading) return (
    <div>
      <PageHeader title="Email" />
      <div className="p-6 space-y-4">
        {Array.from({ length: 4 }).map((_, i) => <Skeleton key={i} className="h-20 rounded-lg" />)}
      </div>
    </div>
  );

  if (error || !data) return (
    <div>
      <PageHeader title="Email" />
      <div className="p-6">
        <p className="text-sm text-destructive">{error ?? 'No data returned'}</p>
      </div>
    </div>
  );

  const g = data.global_smtp;

  return (
    <div>
      <PageHeader
        title="Email"
        description="Global SMTP relay and per-organisation email configuration"
      />

      <div className="p-6 space-y-6">

        {/* ── Summary cards ── */}
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <Card>
            <CardContent className="pt-5">
              <p className="text-xs text-muted-foreground uppercase tracking-wider font-medium mb-1">Global SMTP</p>
              <StatusDot ok={g.configured} label={g.configured ? 'Configured' : 'Not configured'} />
              {g.configured && (
                <p className="text-xs text-muted-foreground mt-1 font-mono truncate">{g.host}:{g.port}</p>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardContent className="pt-5">
              <p className="text-xs text-muted-foreground uppercase tracking-wider font-medium mb-1">Custom SMTP</p>
              <p className="text-2xl font-bold">{customCount}<span className="text-sm font-normal text-muted-foreground"> / {totalOrgs}</span></p>
              <p className="text-xs text-muted-foreground mt-0.5">organisations with own relay</p>
            </CardContent>
          </Card>

          <Card>
            <CardContent className="pt-5">
              <p className="text-xs text-muted-foreground uppercase tracking-wider font-medium mb-1">From-name overrides</p>
              <p className="text-2xl font-bold">{overrideCount}</p>
              <p className="text-xs text-muted-foreground mt-0.5">projects with custom sender name</p>
            </CardContent>
          </Card>
        </div>

        {/* ── Global SMTP detail ── */}
        <Card>
          <CardHeader>
            <div className="flex items-center justify-between">
              <div>
                <CardTitle className="text-base flex items-center gap-2">
                  <Mail className="h-4 w-4" />
                  Global SMTP relay
                </CardTitle>
                <CardDescription>
                  Fallback relay used by organisations that have not configured their own SMTP.
                  Configured via <code className="font-mono text-xs">Smtp__*</code> environment variables.
                </CardDescription>
              </div>
              {g.configured
                ? <Badge className="bg-green-500/15 text-green-700 dark:text-green-400 border-0">Active</Badge>
                : <Badge variant="secondary">Not set</Badge>}
            </div>
          </CardHeader>
          {g.configured && (
            <CardContent>
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-x-6 gap-y-2 text-sm">
                <span className="text-muted-foreground">Host</span>
                <span className="font-mono col-span-1">{g.host}:{g.port}</span>
                <span className="text-muted-foreground">TLS</span>
                <span>{g.start_tls ? 'STARTTLS' : 'None / SSL'}</span>
                <span className="text-muted-foreground">From address</span>
                <span className="font-mono">{g.from_address}</span>
                <span className="text-muted-foreground">From name</span>
                <span>{g.from_name}</span>
              </div>
            </CardContent>
          )}
        </Card>

        {/* ── Org adoption table ── */}
        <Card>
          <CardHeader>
            <CardTitle className="text-base flex items-center gap-2">
              <Building2 className="h-4 w-4" />
              Organisation relay adoption
            </CardTitle>
            <CardDescription>
              Each organisation can override the global relay with their own SMTP settings.
            </CardDescription>
          </CardHeader>
          <CardContent className="p-0">
            {data.orgs.length === 0 ? (
              <p className="text-sm text-muted-foreground text-center py-8">No organisations yet.</p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Organisation</TableHead>
                    <TableHead>SMTP relay</TableHead>
                    <TableHead>From address</TableHead>
                    <TableHead>From name</TableHead>
                    <TableHead>Project overrides</TableHead>
                    <TableHead>Last updated</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.orgs.map(org => (
                    <TableRow key={org.id}>
                      <TableCell>
                        <p className="font-medium">{org.name}</p>
                        <p className="text-xs text-muted-foreground font-mono">{org.slug}</p>
                      </TableCell>

                      <TableCell>
                        {org.smtp_configured
                          ? <StatusDot ok label={`${org.smtp_host}:${org.smtp_port}`} />
                          : <StatusDot ok={false} label="Global" />}
                      </TableCell>

                      <TableCell className="font-mono text-sm">
                        {org.smtp_from_address ?? <span className="text-muted-foreground">{g.from_address ?? '—'}</span>}
                      </TableCell>

                      <TableCell>
                        {org.smtp_from_name ?? <span className="text-muted-foreground">{g.from_name ?? '—'}</span>}
                      </TableCell>

                      <TableCell>
                        {org.project_overrides.length === 0 ? (
                          <span className="text-muted-foreground text-sm">—</span>
                        ) : (
                          <div className="flex flex-wrap gap-1">
                            {org.project_overrides.map(p => (
                              <Badge
                                key={p.id}
                                variant="outline"
                                className="text-xs cursor-pointer hover:bg-accent"
                                title={`From name: "${p.email_from_name}"`}
                                onClick={() => navigate(`/system/organisations/${org.id}/projects`)}
                              >
                                <FolderKanban className="h-3 w-3 mr-1" />
                                {p.name}
                              </Badge>
                            ))}
                          </div>
                        )}
                      </TableCell>

                      <TableCell className="text-muted-foreground text-sm">
                        {org.smtp_updated_at ? fmtDateShort(org.smtp_updated_at) : '—'}
                      </TableCell>

                      <TableCell>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => navigate(`/system/organisations/${org.id}/email`)}
                          title="Configure org SMTP"
                        >
                          <ChevronRight className="h-4 w-4" />
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </CardContent>
        </Card>

      </div>
    </div>
  );
}
