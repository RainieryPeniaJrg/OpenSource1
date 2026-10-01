// app.errores.js — lleva el foco al resumen de errores ([data-resumen-errores], role="alert") de los documentos (spec alta rápida,
// accesibilidad): al cargar la página y tras cada navegación mejorada, que en Static SSR es como llega la respuesta de un POST con
// errores. Se suscribe con Blazor.addEventListener('enhancedload'): 'enhancedload' no se emite en document.
(() => {
  'use strict';

  if (window.__axionErrores) return;
  window.__axionErrores = true;

  function enfocar() {
    const resumen = document.querySelector('[data-resumen-errores]');
    if (resumen instanceof HTMLElement) {
      resumen.focus();
    }
  }

  if (document.readyState === 'loading') {
    window.addEventListener('DOMContentLoaded', enfocar);
  } else {
    enfocar();
  }

  if (window.Blazor?.addEventListener) {
    window.Blazor.addEventListener('enhancedload', enfocar);
  }
})();
