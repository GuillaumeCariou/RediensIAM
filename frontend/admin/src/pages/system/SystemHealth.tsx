import { useEffect, useState } from 'react';
import { RefreshCw, CheckCircle2, XCircle, MinusCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { getSystemHealth } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

type HealthStatus = 'ok' | 'error' | 'not_configured';

interface ComponentHealth {
  name: string;
  category: string;
  status: HealthStatus;
  latency_ms: number | null;
  detail: string | null;
  stats: Record<string, string> | null;
}

interface HealthResponse {
  overall: 'ok' | 'error';
  checks: ComponentHealth[];
}

const STATUS_ICON: Record<HealthStatus, React.ReactNode> = {
  ok:             <CheckCircle2 className="h-5 w-5 text-green-500 shrink-0" />,
  error:          <XCircle      className="h-5 w-5 text-destructive shrink-0" />,
  not_configured: <MinusCircle  className="h-5 w-5 text-muted-foreground shrink-0" />,
};

const STATUS_BADGE: Record<HealthStatus, React.ReactNode> = {
  ok:             <Badge variant="success"   className="text-xs">OK</Badge>,
  error:          <Badge variant="destructive" className="text-xs">Error</Badge>,
  not_configured: <Badge variant="secondary"  className="text-xs">Not configured</Badge>,
};

function ComponentCard({ check }: Readonly<{ check: ComponentHealth }>) {
  return (
    <div className={`rounded-lg border p-4 space-y-3 ${check.status === 'error' ? 'border-destructive/50 bg-destructive/5' : ''}`}>
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2.5 min-w-0">
          {STATUS_ICON[check.status]}
          <span className="font-medium text-sm truncate">{check.name}</span>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {check.latency_ms != null && (
            <span className="text-xs text-muted-foreground tabular-nums">{check.latency_ms} ms</span>
          )}
          {STATUS_BADGE[check.status]}
        </div>
      </div>

      {check.detail && (
        <p className={`text-xs ${check.status === 'error' ? 'text-destructive' : 'text-muted-foreground'}`}>
          {check.detail}
        </p>
      )}

      {check.stats && Object.keys(check.stats).length > 0 && (
        <div className="grid grid-cols-2 gap-x-4 gap-y-1 pt-1 border-t">
          {Object.entries(check.stats).map(([k, v]) => (
            <div key={k} className="flex justify-between items-center gap-2">
              <span className="text-xs text-muted-foreground capitalize">{k.replaceAll('_', ' ')}</span>
              <span className="text-xs font-mono font-medium truncate max-w-[12rem] text-right">{v}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export default function SystemHealth() {
  const [data, setData]       = useState<HealthResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [lastRun, setLastRun] = useState<Date | null>(null);

  const load = () => {
    setLoading(true);
    getSystemHealth()
      .then((d: HealthResponse) => { setData(d); setLastRun(new Date()); })
      .catch(console.error)
      .finally(() => setLoading(false));
  };

  useEffect(load, []);

  const categories = data
    ? [...new Set(data.checks.map(c => c.category))]
    : [];

  return (
    <div>
      <PageHeader
        title="System Health"
        description="Connectivity and status of all backend components"
        action={
          <div className="flex items-center gap-3">
            {lastRun && (
              <span className="text-xs text-muted-foreground">
                Last checked {lastRun.toLocaleTimeString()}
              </span>
            )}
            <Button size="sm" variant="outline" onClick={load} disabled={loading}>
              <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
              Refresh
            </Button>
          </div>
        }
      />

      <div className="p-6 space-y-6">
        {/* Overall status banner */}
        {!loading && data && (
          <div className={`flex items-center gap-3 rounded-lg border px-4 py-3 ${
            data.overall === 'ok'
              ? 'border-green-200 bg-green-50 dark:border-green-900 dark:bg-green-950/30'
              : 'border-destructive/40 bg-destructive/5'
          }`}>
            {data.overall === 'ok'
              ? <CheckCircle2 className="h-5 w-5 text-green-600" />
              : <XCircle      className="h-5 w-5 text-destructive" />}
            <span className="text-sm font-medium">
              {data.overall === 'ok'
                ? 'All systems operational'
                : `${data.checks.filter(c => c.status === 'error').length} component(s) have errors`}
            </span>
          </div>
        )}

        {(() => {
          if (loading) return (
            <div className="grid gap-4 md:grid-cols-2">
              {Array.from({ length: 6 }, (_, i) => `sk-row-${i}`).map(rowId => (
                <Skeleton key={rowId} className="h-24 w-full" />
              ))}
            </div>
          );
          if (data) return (
            <div className="space-y-6">
              {categories.map(cat => (
                <Card key={cat}>
                  <CardHeader className="pb-3">
                    <CardTitle className="text-sm font-semibold text-muted-foreground uppercase tracking-wide">
                      {cat}
                    </CardTitle>
                  </CardHeader>
                  <CardContent className="grid gap-3 md:grid-cols-2">
                    {data.checks.filter(c => c.category === cat).map(check => (
                      <ComponentCard key={check.name} check={check} />
                    ))}
                  </CardContent>
                </Card>
              ))}
            </div>
          );
          return null;
        })()}
      </div>
    </div>
  );
}
