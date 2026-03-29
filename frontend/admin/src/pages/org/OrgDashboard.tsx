import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { FolderKanban, List, Shield, ArrowRight } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { getOrgInfo, listProjects, listUserLists } from '@/api';
import { useOrgContext } from '@/hooks/useOrgContext';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDateShort } from '@/lib/utils';

interface Org {
  id: string; name: string; slug: string; active: boolean; suspended_at: string | null;
  created_at: string; metadata: Record<string, string>;
}
interface Project { id: string; name: string; slug: string; active: boolean; }
interface UserList { id: string; name: string; immovable: boolean; user_count: number; }

export default function OrgDashboard() {
  const { orgId, orgBase, projectUrl } = useOrgContext();
  const [org, setOrg] = useState<Org | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [lists, setLists] = useState<UserList[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!orgId) { setLoading(false); return; }
    Promise.all([
      getOrgInfo().then(setOrg),
      listProjects(orgId).then(r => setProjects(r.projects ?? r ?? [])),
      listUserLists(orgId).then(r => setLists(r.user_lists ?? r ?? [])),
    ]).catch(console.error).finally(() => setLoading(false));
  }, [orgId]);

  if (!orgId) {
    return (
      <div>
        <PageHeader title="Organisation" />
        <div className="p-6 text-muted-foreground text-sm">No organisation selected. Navigate here from <Link to="/system/organisations" className="text-primary underline">Organisations</Link>.</div>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title={loading ? 'Loading…' : (org?.name ?? 'Organisation')}
        description={org ? `/${org.slug}` : undefined}
        action={
          org && (
            <div className="flex gap-2 items-center">
              {org.suspended_at
                ? <Badge variant="destructive">Suspended</Badge>
                : org.active
                ? <Badge variant="success">Active</Badge>
                : <Badge variant="secondary">Inactive</Badge>
              }
            </div>
          )
        }
      />
      <div className="p-6 space-y-6">
        {loading ? (
          <div className="grid grid-cols-3 gap-4">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-32 rounded-xl" />)}</div>
        ) : (
          <>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><FolderKanban className="h-4 w-4" />Projects</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-2xl font-bold">{projects.length}</p>
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={`${orgBase}/projects`}>Manage <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                </CardContent>
              </Card>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><List className="h-4 w-4" />User Lists</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-2xl font-bold">{lists.length}</p>
                  <Button variant="ghost" size="sm" className="mt-2 -ml-2 text-xs" asChild>
                    <Link to={`${orgBase}/userlists`}>Manage <ArrowRight className="h-3 w-3" /></Link>
                  </Button>
                </CardContent>
              </Card>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm text-muted-foreground flex items-center gap-2"><Shield className="h-4 w-4" />Details</CardTitle></CardHeader>
                <CardContent>
                  <p className="text-sm text-muted-foreground">Since</p>
                  <p className="font-medium">{fmtDateShort(org?.created_at)}</p>
                </CardContent>
              </Card>
            </div>

            {/* Recent projects */}
            {projects.length > 0 && (
              <Card>
                <CardHeader><CardTitle className="text-base">Projects</CardTitle></CardHeader>
                <CardContent>
                  <div className="space-y-2">
                    {projects.map(p => (
                      <div key={p.id} className="flex items-center justify-between py-2 border-b last:border-0">
                        <div>
                          <p className="font-medium text-sm">{p.name}</p>
                          <p className="text-xs text-muted-foreground font-mono">{p.slug}</p>
                        </div>
                        <div className="flex items-center gap-2">
                          {p.active ? <Badge variant="success">Active</Badge> : <Badge variant="secondary">Inactive</Badge>}
                          <Button variant="ghost" size="sm" asChild>
                            <Link to={projectUrl(p.id)}>Open <ArrowRight className="h-3 w-3" /></Link>
                          </Button>
                        </div>
                      </div>
                    ))}
                  </div>
                </CardContent>
              </Card>
            )}
          </>
        )}
      </div>
    </div>
  );
}
