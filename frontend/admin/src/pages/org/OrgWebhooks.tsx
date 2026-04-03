import { useEffect, useState } from 'react';
import { Plus, MoreHorizontal, Trash2, Play, List as ListIcon, ChevronDown, ChevronRight } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Switch } from '@/components/ui/switch';
import { Input } from '@/components/ui/input';
import { Alert } from '@/components/ui/alert';
import { Skeleton } from '@/components/ui/skeleton';
import { listWebhooks, createWebhook, updateWebhook, deleteWebhook, testWebhook, listWebhookDeliveries } from '@/api';
import PageHeader from '@/components/layout/PageHeader';
import { fmtDate } from '@/lib/utils';

interface Webhook {
  id: string;
  url: string;
  events: string[];
  active: boolean;
  last_delivery_status?: number | null;
  created_at: string;
}

interface Delivery {
  id: string;
  event: string;
  status_code: number | null;
  attempt_count: number;
  delivered_at: string | null;
  payload?: string | null;
}

const EVENT_GROUPS: { label: string; events: string[] }[] = [
  { label: 'User events', events: ['user.created', 'user.updated', 'user.deleted', 'user.locked', 'user.login.success', 'user.login.failure'] },
  { label: 'Role events', events: ['role.assigned', 'role.revoked'] },
  { label: 'Session events', events: ['session.revoked'] },
  { label: 'Project events', events: ['project.updated'] },
];
const ALL_EVENTS = EVENT_GROUPS.flatMap(g => g.events);

export default function OrgWebhooks() {
  const [webhooks, setWebhooks] = useState<Webhook[]>([]);
  const [loading, setLoading] = useState(true);

  // Create dialog
  const [addOpen, setAddOpen] = useState(false);
  const [newUrl, setNewUrl] = useState('');
  const [newEvents, setNewEvents] = useState<string[]>([]);
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState('');

  // Secret reveal after creation
  const [secretOpen, setSecretOpen] = useState(false);
  const [newSecret, setNewSecret] = useState('');

  // Deliveries dialog
  const [deliveriesOpen, setDeliveriesOpen] = useState(false);
  const [deliveriesFor, setDeliveriesFor] = useState<string>('');
  const [deliveries, setDeliveries] = useState<Delivery[]>([]);
  const [deliveriesLoading, setDeliveriesLoading] = useState(false);
  const [expandedDelivery, setExpandedDelivery] = useState<string | null>(null);

  // Test feedback
  const [testMsg, setTestMsg] = useState<{ id: string; ok: boolean; text: string } | null>(null);

  const load = () => {
    setLoading(true);
    listWebhooks()
      .then((d: Webhook[]) => setWebhooks(Array.isArray(d) ? d : []))
      .catch(console.error)
      .finally(() => setLoading(false));
  };
  useEffect(load, []);

  const handleCreate = async () => {
    setCreateError('');
    if (!newUrl.startsWith('https://')) { setCreateError('URL must use HTTPS.'); return; }
    if (newEvents.length === 0) { setCreateError('Select at least one event.'); return; }
    setCreating(true);
    try {
      const res = await createWebhook({ url: newUrl, events: newEvents });
      if (res.error) { setCreateError(res.error_description ?? 'Failed to create webhook.'); return; }
      setAddOpen(false);
      setNewUrl(''); setNewEvents([]);
      if (res.secret) { setNewSecret(res.secret); setSecretOpen(true); }
      load();
    } finally { setCreating(false); }
  };

  const handleToggleActive = async (wh: Webhook) => {
    setWebhooks(ws => ws.map(w => w.id === wh.id ? { ...w, active: !w.active } : w));
    await updateWebhook(wh.id, { active: !wh.active });
  };

  const handleDelete = async (id: string) => {
    await deleteWebhook(id);
    setWebhooks(ws => ws.filter(w => w.id !== id));
  };

  const handleTest = async (id: string) => {
    setTestMsg(null);
    const res = await testWebhook(id);
    if (res.error) {
      setTestMsg({ id, ok: false, text: `Test failed: ${res.error}` });
    } else {
      setTestMsg({ id, ok: true, text: 'Test payload sent.' });
    }
    setTimeout(() => setTestMsg(null), 4000);
  };

  const openDeliveries = (id: string) => {
    setDeliveriesFor(id);
    setDeliveriesOpen(true);
    setExpandedDelivery(null);
    setDeliveriesLoading(true);
    listWebhookDeliveries(id)
      .then((d: Delivery[]) => setDeliveries(Array.isArray(d) ? d : []))
      .catch(console.error)
      .finally(() => setDeliveriesLoading(false));
  };

  const toggleEventSelection = (ev: string) => {
    setNewEvents(evs => evs.includes(ev) ? evs.filter(e => e !== ev) : [...evs, ev]);
  };

  const toggleGroup = (events: string[]) => {
    const allSelected = events.every(e => newEvents.includes(e));
    if (allSelected) setNewEvents(evs => evs.filter(e => !events.includes(e)));
    else setNewEvents(evs => [...new Set([...evs, ...events])]);
  };

  return (
    <div>
      <PageHeader title="Webhooks" description="Receive HTTP notifications when events occur" />
      <div className="p-6 space-y-4">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-4">
            <CardTitle className="text-base">Webhooks</CardTitle>
            <Button size="sm" onClick={() => { setCreateError(''); setAddOpen(true); }}>
              <Plus className="h-4 w-4" />Add Webhook
            </Button>
          </CardHeader>
          <CardContent className="p-0">
            {loading ? (
              <div className="p-4 space-y-2">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}</div>
            ) : webhooks.length === 0 ? (
              <p className="text-center text-sm text-muted-foreground py-10">
                No webhooks configured. Add one to receive event notifications.
              </p>
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>URL</TableHead>
                    <TableHead>Events</TableHead>
                    <TableHead>Active</TableHead>
                    <TableHead>Last status</TableHead>
                    <TableHead>Created</TableHead>
                    <TableHead />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {webhooks.map(wh => (
                    <>
                      <TableRow key={wh.id}>
                        <TableCell className="font-mono text-xs max-w-xs truncate">{wh.url}</TableCell>
                        <TableCell>
                          <div className="flex flex-wrap gap-1 max-w-xs">
                            {wh.events.slice(0, 3).map(e => <Badge key={e} variant="secondary" className="text-xs font-mono">{e}</Badge>)}
                            {wh.events.length > 3 && <Badge variant="outline" className="text-xs">+{wh.events.length - 3} more</Badge>}
                          </div>
                        </TableCell>
                        <TableCell>
                          <Switch checked={wh.active} onCheckedChange={() => handleToggleActive(wh)} />
                        </TableCell>
                        <TableCell>
                          {wh.last_delivery_status != null ? (
                            <Badge variant={wh.last_delivery_status >= 200 && wh.last_delivery_status < 300 ? 'success' : 'destructive'} className="text-xs font-mono">
                              {wh.last_delivery_status}
                            </Badge>
                          ) : <span className="text-xs text-muted-foreground">—</span>}
                        </TableCell>
                        <TableCell className="text-xs text-muted-foreground">{fmtDate(wh.created_at)}</TableCell>
                        <TableCell className="text-right">
                          {testMsg?.id === wh.id && (
                            <span className={`text-xs mr-2 ${testMsg.ok ? 'text-green-600' : 'text-destructive'}`}>
                              {testMsg.text}
                            </span>
                          )}
                          <DropdownMenu>
                            <DropdownMenuTrigger asChild>
                              <Button variant="ghost" size="icon" className="h-8 w-8">
                                <MoreHorizontal className="h-4 w-4" />
                              </Button>
                            </DropdownMenuTrigger>
                            <DropdownMenuContent align="end">
                              <DropdownMenuItem onClick={() => handleTest(wh.id)}>
                                <Play className="h-4 w-4" />Test
                              </DropdownMenuItem>
                              <DropdownMenuItem onClick={() => openDeliveries(wh.id)}>
                                <ListIcon className="h-4 w-4" />View deliveries
                              </DropdownMenuItem>
                              <DropdownMenuSeparator />
                              <DropdownMenuItem
                                className="text-destructive focus:text-destructive"
                                onClick={() => handleDelete(wh.id)}
                              >
                                <Trash2 className="h-4 w-4" />Delete
                              </DropdownMenuItem>
                            </DropdownMenuContent>
                          </DropdownMenu>
                        </TableCell>
                      </TableRow>
                    </>
                  ))}
                </TableBody>
              </Table>
            )}
          </CardContent>
        </Card>
      </div>

      {/* Create dialog */}
      <Dialog open={addOpen} onOpenChange={setAddOpen}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Add Webhook</DialogTitle>
            <DialogDescription>Receive HTTP POST notifications when events occur in your organisation.</DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-2">
            {createError && <Alert variant="destructive" className="text-sm py-2 px-3">{createError}</Alert>}
            <div className="space-y-2">
              <label className="text-sm font-medium">URL</label>
              <Input
                type="url"
                placeholder="https://example.com/webhook"
                value={newUrl}
                onChange={e => setNewUrl(e.target.value)}
              />
            </div>
            <div className="space-y-3">
              <label className="text-sm font-medium">Events</label>
              {EVENT_GROUPS.map(group => {
                const allChecked = group.events.every(e => newEvents.includes(e));
                const someChecked = group.events.some(e => newEvents.includes(e));
                return (
                  <div key={group.label} className="space-y-1.5">
                    <label className="flex items-center gap-2 text-xs font-semibold text-muted-foreground cursor-pointer">
                      <input
                        type="checkbox"
                        checked={allChecked}
                        ref={el => { if (el) el.indeterminate = someChecked && !allChecked; }}
                        onChange={() => toggleGroup(group.events)}
                      />
                      {group.label}
                    </label>
                    <div className="grid grid-cols-2 gap-1 pl-4">
                      {group.events.map(ev => (
                        <label key={ev} className="flex items-center gap-2 text-xs cursor-pointer">
                          <input
                            type="checkbox"
                            checked={newEvents.includes(ev)}
                            onChange={() => toggleEventSelection(ev)}
                          />
                          <span className="font-mono">{ev}</span>
                        </label>
                      ))}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setAddOpen(false)}>Cancel</Button>
            <Button onClick={handleCreate} disabled={creating}>
              {creating ? 'Creating…' : 'Create Webhook'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Secret reveal dialog */}
      <Dialog open={secretOpen} onOpenChange={setSecretOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Webhook Secret</DialogTitle>
            <DialogDescription>Copy this now — it won't be shown again. Use it to verify webhook signatures.</DialogDescription>
          </DialogHeader>
          <div className="rounded-lg bg-muted p-4 font-mono text-sm break-all">{newSecret}</div>
          <DialogFooter>
            <Button variant="outline" onClick={() => { navigator.clipboard.writeText(newSecret); }}>Copy</Button>
            <Button onClick={() => { setSecretOpen(false); setNewSecret(''); }}>I've saved it</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Deliveries dialog */}
      <Dialog open={deliveriesOpen} onOpenChange={setDeliveriesOpen}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>Delivery Log</DialogTitle>
            <DialogDescription>Last 25 deliveries for this webhook.</DialogDescription>
          </DialogHeader>
          {deliveriesLoading ? (
            <div className="space-y-2">{Array.from({ length: 5 }).map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}</div>
          ) : deliveries.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4 text-center">No deliveries yet.</p>
          ) : (
            <div className="space-y-1 max-h-96 overflow-y-auto">
              {deliveries.map(d => (
                <div key={d.id} className="rounded-lg border">
                  <button
                    className="flex w-full items-center gap-3 px-4 py-2.5 text-sm hover:bg-muted/50 text-left"
                    onClick={() => setExpandedDelivery(expandedDelivery === d.id ? null : d.id)}
                  >
                    <span className="font-mono text-xs flex-1">{d.event}</span>
                    {d.status_code != null ? (
                      <Badge variant={d.status_code >= 200 && d.status_code < 300 ? 'success' : 'destructive'} className="text-xs font-mono">
                        {d.status_code}
                      </Badge>
                    ) : <Badge variant="secondary" className="text-xs">pending</Badge>}
                    <span className="text-xs text-muted-foreground">{d.attempt_count} attempt{d.attempt_count !== 1 ? 's' : ''}</span>
                    <span className="text-xs text-muted-foreground">{d.delivered_at ? fmtDate(d.delivered_at) : '—'}</span>
                    {expandedDelivery === d.id ? <ChevronDown className="h-3 w-3 shrink-0" /> : <ChevronRight className="h-3 w-3 shrink-0" />}
                  </button>
                  {expandedDelivery === d.id && d.payload && (
                    <pre className="text-xs bg-muted p-4 rounded-b-lg overflow-x-auto whitespace-pre-wrap border-t">
                      {(() => { try { return JSON.stringify(JSON.parse(d.payload), null, 2); } catch { return d.payload; } })()}
                    </pre>
                  )}
                </div>
              ))}
            </div>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={() => setDeliveriesOpen(false)}>Close</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
