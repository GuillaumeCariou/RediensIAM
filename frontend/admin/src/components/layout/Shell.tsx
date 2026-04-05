import Sidebar from './Sidebar';

export default function Shell({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <div className="flex h-screen overflow-hidden">
      <Sidebar />
      <main className="flex flex-1 flex-col overflow-y-auto bg-muted/30">
        {children}
      </main>
    </div>
  );
}
