const toggle = document.getElementById('navigation-toggle')!;
const sidebar = document.getElementById('dashboard-sidebar')!;
const overlay = document.getElementById('navigation-overlay')!;
const narrowScreen = window.matchMedia('(max-width: 991.98px)');

function setOpen(open: boolean) {
  document.body.classList.toggle('sidebar-open', open);
  toggle.setAttribute('aria-expanded', String(open));
  overlay.hidden = !open;
  sidebar.inert = narrowScreen.matches && !open;
}

function close() {
  setOpen(false);
  toggle.focus();
}

toggle.addEventListener('click', () => {
  const open = !document.body.classList.contains('sidebar-open');
  setOpen(open);
  if (open) sidebar.querySelector<HTMLAnchorElement>('a[href]')?.focus();
});

overlay.addEventListener('click', close);
sidebar.addEventListener('click', event => {
  if (narrowScreen.matches && event.target instanceof Element && event.target.closest('a[href]')) {
    close();
  }
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && document.body.classList.contains('sidebar-open')) {
    close();
  }
});
narrowScreen.addEventListener('change', () => setOpen(false));
setOpen(false);
