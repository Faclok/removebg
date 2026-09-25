import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './styles.css';

const canonicalUrl = new URL(window.location.pathname, window.location.origin).href;
const canonical = document.createElement('link');
canonical.rel = 'canonical';
canonical.href = canonicalUrl;
document.head.append(canonical);

const openGraphUrl = document.createElement('meta');
openGraphUrl.setAttribute('property', 'og:url');
openGraphUrl.content = canonicalUrl;
document.head.append(openGraphUrl);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
