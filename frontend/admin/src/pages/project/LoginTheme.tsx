import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Save, Eye } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Textarea } from '@/components/ui/textarea';
import { Skeleton } from '@/components/ui/skeleton';
import { getProject, updateProject } from '@/api';
import PageHeader from '@/components/layout/PageHeader';

interface Theme {
  primary_color?: string;
  background_color?: string;
  font_family?: string;
  logo_url?: string;
  custom_css?: string;
}

export default function LoginTheme() {
  const [params] = useSearchParams();
  const projectId = params.get('project_id') ?? '';
  const [theme, setTheme] = useState<Theme>({
    primary_color: '#1a56db',
    background_color: '#f9fafb',
    font_family: 'Inter',
    logo_url: '',
    custom_css: '',
  });
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (!projectId) { setLoading(false); return; }
    getProject(projectId).then(p => {
      if (p.login_theme) setTheme(t => ({ ...t, ...p.login_theme }));
    }).catch(console.error).finally(() => setLoading(false));
  }, [projectId]);

  const handleSave = async () => {
    setSaving(true);
    try {
      await updateProject(projectId, { login_theme: theme as Record<string, string> });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } finally { setSaving(false); }
  };

  if (loading) return (
    <div>
      <PageHeader title="Login Theme" />
      <div className="p-6 space-y-4">{Array.from({ length: 4 }).map((_, i) => <Skeleton key={i} className="h-12 rounded-lg" />)}</div>
    </div>
  );

  return (
    <div>
      <PageHeader
        title="Login Page Theme"
        description="Customize the appearance of the login page for this project"
        action={
          <Button onClick={handleSave} disabled={saving}>
            <Save className="h-4 w-4" />{saving ? 'Saving…' : saved ? 'Saved!' : 'Save Changes'}
          </Button>
        }
      />
      <div className="p-6">
        <Tabs defaultValue="visual">
          <TabsList>
            <TabsTrigger value="visual">Visual</TabsTrigger>
            <TabsTrigger value="css">Custom CSS</TabsTrigger>
            <TabsTrigger value="preview">Preview</TabsTrigger>
          </TabsList>

          <TabsContent value="visual" className="mt-6">
            <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
              <Card>
                <CardHeader><CardTitle className="text-base">Colors</CardTitle></CardHeader>
                <CardContent className="space-y-4">
                  <div className="space-y-2">
                    <Label>Primary Color</Label>
                    <div className="flex gap-2 items-center">
                      <input type="color" value={theme.primary_color ?? '#1a56db'} onChange={e => setTheme(t => ({ ...t, primary_color: e.target.value }))} className="h-9 w-14 rounded cursor-pointer border border-input" />
                      <Input value={theme.primary_color ?? ''} onChange={e => setTheme(t => ({ ...t, primary_color: e.target.value }))} className="font-mono" />
                    </div>
                  </div>
                  <div className="space-y-2">
                    <Label>Background Color</Label>
                    <div className="flex gap-2 items-center">
                      <input type="color" value={theme.background_color ?? '#f9fafb'} onChange={e => setTheme(t => ({ ...t, background_color: e.target.value }))} className="h-9 w-14 rounded cursor-pointer border border-input" />
                      <Input value={theme.background_color ?? ''} onChange={e => setTheme(t => ({ ...t, background_color: e.target.value }))} className="font-mono" />
                    </div>
                  </div>
                </CardContent>
              </Card>

              <Card>
                <CardHeader><CardTitle className="text-base">Branding</CardTitle></CardHeader>
                <CardContent className="space-y-4">
                  <div className="space-y-2">
                    <Label>Logo URL</Label>
                    <Input value={theme.logo_url ?? ''} onChange={e => setTheme(t => ({ ...t, logo_url: e.target.value }))} placeholder="https://cdn.example.com/logo.png" />
                    {theme.logo_url && (
                      <div className="mt-2 flex items-center gap-2">
                        <Eye className="h-3 w-3 text-muted-foreground" />
                        <img src={theme.logo_url} alt="Logo preview" className="h-8 object-contain" onError={e => (e.currentTarget.style.display = 'none')} />
                      </div>
                    )}
                  </div>
                  <div className="space-y-2">
                    <Label>Font Family</Label>
                    <Input value={theme.font_family ?? ''} onChange={e => setTheme(t => ({ ...t, font_family: e.target.value }))} placeholder="Inter" />
                  </div>
                </CardContent>
              </Card>
            </div>
          </TabsContent>

          <TabsContent value="css" className="mt-6">
            <Card>
              <CardHeader>
                <CardTitle className="text-base">Custom CSS</CardTitle>
                <CardDescription>Injected into the login page &lt;head&gt;. Use CSS variables like --primary, --background.</CardDescription>
              </CardHeader>
              <CardContent>
                <Textarea
                  value={theme.custom_css ?? ''}
                  onChange={e => setTheme(t => ({ ...t, custom_css: e.target.value }))}
                  className="font-mono text-sm min-h-[300px]"
                  placeholder={`.login-card {\n  border-radius: 16px;\n  box-shadow: 0 20px 60px rgba(0,0,0,0.15);\n}`}
                />
              </CardContent>
            </Card>
          </TabsContent>

          <TabsContent value="preview" className="mt-6">
            <Card>
              <CardHeader><CardTitle className="text-base">Preview</CardTitle><CardDescription>Approximate look of the login page.</CardDescription></CardHeader>
              <CardContent>
                <div
                  className="rounded-xl p-8 flex items-center justify-center min-h-[400px]"
                  style={{ background: theme.background_color }}
                >
                  <div className="w-80 bg-white rounded-xl p-8 shadow-lg space-y-4" style={{ fontFamily: theme.font_family }}>
                    {theme.logo_url && <img src={theme.logo_url} alt="Logo" className="h-10 mx-auto object-contain" />}
                    <h1 className="text-xl font-bold text-center text-gray-900">Sign in</h1>
                    <p className="text-sm text-center text-gray-500">Enter your credentials to continue</p>
                    <div className="space-y-3">
                      <div className="h-9 rounded-md border border-gray-200 bg-gray-50" />
                      <div className="h-9 rounded-md border border-gray-200 bg-gray-50" />
                      <div className="h-9 rounded-md text-white text-sm font-medium flex items-center justify-center" style={{ background: theme.primary_color }}>
                        Sign in
                      </div>
                    </div>
                  </div>
                </div>
              </CardContent>
            </Card>
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}
