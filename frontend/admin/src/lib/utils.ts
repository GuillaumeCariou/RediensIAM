import { type ClassValue, clsx } from 'clsx';
import { twMerge } from 'tailwind-merge';

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

export function fmtDate(d: string | null | undefined) {
  if (!d) return '—';
  return new Date(d).toLocaleString();
}

export function fmtDateShort(d: string | null | undefined) {
  if (!d) return '—';
  return new Date(d).toLocaleDateString();
}
