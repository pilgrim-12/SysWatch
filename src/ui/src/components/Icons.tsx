const base = { fill: 'none', stroke: 'currentColor', strokeWidth: 1.6, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const, viewBox: '0 0 24 24' };

export const IconPulse = () => <svg {...base}><path d="M3 12h4l2-6 4 12 2-6h6" /></svg>;
export const IconHeat = () => <svg {...base}><rect x="3" y="4" width="5" height="5" rx="1" /><rect x="10" y="4" width="5" height="5" rx="1" /><rect x="17" y="4" width="4" height="5" rx="1" /><rect x="3" y="11" width="5" height="5" rx="1" /><rect x="10" y="11" width="5" height="5" rx="1" /><rect x="3" y="18" width="5" height="3" rx="1" /></svg>;
export const IconNet = () => <svg {...base}><circle cx="5" cy="12" r="2" /><circle cx="19" cy="5" r="2" /><circle cx="19" cy="12" r="2" /><circle cx="19" cy="19" r="2" /><path d="M7 12h10M7 11l10-5M7 13l10 5" /></svg>;
export const IconList = () => <svg {...base}><path d="M8 6h13M8 12h13M8 18h13" /><circle cx="4" cy="6" r="1" /><circle cx="4" cy="12" r="1" /><circle cx="4" cy="18" r="1" /></svg>;
export const IconAlert = () => <svg {...base}><path d="M12 3 2.5 20h19L12 3Z" /><path d="M12 10v4" /><circle cx="12" cy="17" r="0.6" /></svg>;
export const IconDisk = () => <svg {...base}><rect x="3" y="5" width="18" height="14" rx="2" /><path d="M3 14h18" /><circle cx="17" cy="16.5" r="0.8" /><path d="M6 16.5h6" /></svg>;
export const IconLink = () => <svg {...base}><path d="M4 17 10 11" /><path d="M7 11h3v3" /><path d="M20 7l-6 6" /><path d="M17 13h-3v-3" /><circle cx="4" cy="17" r="1.6" /><circle cx="20" cy="7" r="1.6" /></svg>;
export const IconChip = () => <svg {...base}><rect x="6" y="6" width="12" height="12" rx="2" /><rect x="9.5" y="9.5" width="5" height="5" rx="1" /><path d="M9 3v3M15 3v3M9 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3" /></svg>;
export const IconApps = () => <svg {...base}><rect x="3" y="3" width="8" height="8" rx="2" /><rect x="13" y="3" width="8" height="8" rx="2" /><rect x="3" y="13" width="8" height="8" rx="2" /><rect x="13" y="13" width="8" height="8" rx="2" /></svg>;
export const IconCode = () => <svg {...base}><path d="M8 7l-5 5 5 5M16 7l5 5-5 5M13.5 5l-3 14" /></svg>;
export const IconClock = () => <svg {...base}><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></svg>;
export const IconGpu = () => <svg {...base}><rect x="2.5" y="6" width="19" height="11" rx="2" /><circle cx="9" cy="11.5" r="2.6" /><path d="M14.5 9.5h4M14.5 13.5h4M5 17v2M9 17v2" /></svg>;
export const IconReport = () => <svg {...base}><path d="M7 3h7l5 5v13H7z" /><path d="M14 3v5h5" /><path d="M10 17v-3M13 17v-5M16 17v-2" /></svg>;

export const BrandMark = () => (
  <svg className="brand-mark" viewBox="0 0 24 24" fill="none">
    <rect x="1" y="1" width="22" height="22" rx="6" stroke="#4fc3d9" strokeWidth="1.5" />
    <path d="M4 14h4l2-6 3 9 2-5h5" stroke="#f2b544" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);
