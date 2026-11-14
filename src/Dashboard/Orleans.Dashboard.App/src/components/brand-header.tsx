import React from 'react';

// Served from public/ so the build copies it verbatim, keeping the bundle and source map deterministic.
const logo = `${import.meta.env.BASE_URL}img/OrleansLogo.png`;

export default function BrandHeader() {
  return (
    <div className="brand-link">
      <a href="#" style={{ textDecoration: 'none' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
          <img src={logo} alt="Orleans Logo" className="brand-logo" />
          <h1 className="brand-title">
            Orleans Dashboard
          </h1>
        </div>
      </a>
      <div id="version-content" className="brand-version"></div>
    </div>
  );
}
