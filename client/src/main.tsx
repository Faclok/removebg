import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './styles.css';

// Production Nginx replaces this placeholder before crawlers receive the HTML.
// This fallback keeps metadata correct when the app runs through Vite locally.
document.querySelectorAll<HTMLMetaElement>('meta[content*="__SITE_URL__"]').forEach((element) => {
  element.content = element.content.replace('__SITE_URL__', window.location.origin);
});
document.querySelectorAll<HTMLLinkElement>('link[href*="__SITE_URL__"]').forEach((element) => {
  const href = element.getAttribute('href');
  if (href) element.setAttribute('href', href.replace('__SITE_URL__', window.location.origin));
});
document.querySelectorAll<HTMLScriptElement>('script[type="application/ld+json"]').forEach((element) => {
  if (element.textContent?.includes('__SITE_URL__')) element.textContent = element.textContent.replaceAll('__SITE_URL__', window.location.origin);
});

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
