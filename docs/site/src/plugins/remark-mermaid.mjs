const htmlEscapePattern = /[&<>"']/g;
const htmlEscapes = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
};

function escapeHtml(value) {
  return value.replace(htmlEscapePattern, (character) => htmlEscapes[character]);
}

function transformMermaidBlocks(node) {
  if (node.type === 'code' && node.lang === 'mermaid') {
    const source = escapeHtml(node.value);
    node.type = 'html';
    node.value =
      `<div class="mermaid-diagram" data-mermaid-source="${source}" ` +
      'data-mermaid-status="loading" aria-label="Mermaid diagram">' +
      '<span class="sr-only">Rendering diagram.</span>' +
      `<noscript><pre><code>${source}</code></pre></noscript></div>`;
    delete node.lang;
    delete node.meta;
    return;
  }

  for (const child of node.children ?? []) {
    transformMermaidBlocks(child);
  }
}

export function remarkMermaid() {
  return (tree) => transformMermaidBlocks(tree);
}
