import { beforeAll, describe, expect, it, vi } from 'vitest';

let d: typeof import('../src/pathLabel').describePath;
beforeAll(async () => {
  vi.stubGlobal('localStorage', { getItem: () => 'ru', setItem: () => {} });
  vi.resetModules();
  d = (await import('../src/pathLabel')).describePath;
});

describe('describePath', () => {
  it('recognises Edge, WebView2 and EdgeCore updates with the version', () => {
    expect(d('C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\156.0.4314.42')?.label).toBe('Обновление Microsoft Edge 156.0.4314.42');
    expect(d('C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\156.0.4314.42')?.label).toBe('Обновление WebView2 156.0.4314.42');
    expect(d('C:\\Program Files (x86)\\Microsoft\\EdgeCore\\156.0.4314.42')?.kind).toBe('update');
  });

  it('recognises temp, caches and dev folders', () => {
    expect(d('C:\\Users\\Yurii\\AppData\\Local\\Temp')?.kind).toBe('temp');
    expect(d('C:\\Users\\Yurii\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Cache')?.label).toBe('Кэш Google Chrome');
    expect(d('C:\\Users\\Yurii\\AppData\\Local\\Google\\Chrome\\User Data')?.label).toBe('Профиль Google Chrome');
    expect(d('C:\\Users\\Yurii\\.nuget\\packages')?.kind).toBe('dev');
    expect(d('C:\\Windows\\SoftwareDistribution\\Download')?.kind).toBe('update');
    expect(d('D:\\src\\app\\node_modules')?.label).toContain('node_modules');
  });

  it('returns null for ordinary folders', () => {
    expect(d('C:\\Users\\Yurii\\Documents\\Projects')).toBeNull();
    expect(d(null)).toBeNull();
  });
});
