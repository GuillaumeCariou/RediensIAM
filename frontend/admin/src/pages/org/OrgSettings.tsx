import { useEffect, useState } from 'react';
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { getOrgInfo, updateOrgInfo } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

export default function OrgSettings() {
  const [retentionDays, setRetentionDays] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    getOrgInfo()
      .then((d: { audit_retention_days?: number | null }) => {
        setRetentionDays(d.audit_retention_days ?? null);
      })
      .catch(console.error)
      .finally(() => setLoading(false));
  }, []);

  const save = async () => {
    setSaving(true);
    try {
      await updateOrgInfo({ audit_retention_days: retentionDays });
      setSaved(true);
      setTimeout(() => setSaved(false), 3000);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div>
      <PageHeader title="Settings" description="Organisation-level configuration" />
      <div className="p-6 max-w-xl space-y-6">
        {loading ? (
          <Skeleton className="h-40 rounded-xl" />
        ) : (
          <Card>
            <CardHeader>
              <CardTitle>Audit Log Retention</CardTitle>
              <CardDescription>
                Audit logs older than the retention period are automatically deleted.
                Set to "Forever" to disable automatic deletion.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Select
                value={retentionDays == null ? '' : String(retentionDays)}
                onValueChange={v => setRetentionDays(v === '' ? null : Number(v))}
              >
                <SelectTrigger className="w-48">
                  <SelectValue placeholder="Select period" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="30">30 days</SelectItem>
                  <SelectItem value="60">60 days</SelectItem>
                  <SelectItem value="90">90 days</SelectItem>
                  <SelectItem value="180">180 days</SelectItem>
                  <SelectItem value="365">1 year</SelectItem>
                  <SelectItem value="">Forever</SelectItem>
                </SelectContent>
              </Select>
            </CardContent>
            <CardFooter className="flex items-center gap-3">
              <Button onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Save'}</Button>
              {saved && <span className="text-sm text-green-600">Saved!</span>}
            </CardFooter>
          </Card>
        )}
      </div>
    </div>
  );
}
