// Below its lg breakpoint, AdminLTE's CSS hides the sidebar unless the body has sidebar-open.
// The dashboard does not load AdminLTE's JavaScript, so it toggles that class itself.
export default function initializeSidebar() {
  const body = document.body;
  const toggle = document.getElementById('sidebar-toggle')!;

  const setOpen = (open: boolean) => {
    body.classList.toggle('sidebar-open', open);
    toggle.setAttribute('aria-expanded', String(open));
  };

  toggle.addEventListener('click', () => setOpen(!body.classList.contains('sidebar-open')));
  document.getElementById('sidebar-overlay')!.addEventListener('click', () => setOpen(false));
  document.getElementById('dashboard-sidebar')!.addEventListener('click', event => {
    if ((event.target as Element).closest('a')) setOpen(false);
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') setOpen(false);
  });
}
