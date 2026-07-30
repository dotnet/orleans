function visit(node) {
  if (!node || typeof node !== 'object') {
    return;
  }

  if (node.type === 'containerDirective' && node.name === 'version') {
    const versions = node.attributes?.versions;
    if (typeof versions !== 'string' || versions.length === 0) {
      throw new Error('A generated version zone is missing its versions attribute.');
    }

    node.data = {
      ...node.data,
      hName: 'section',
      hProperties: {
        className: ['version-zone'],
        dataVersions: versions,
      },
    };
    node.children.unshift({
      type: 'paragraph',
      data: {
        hName: 'p',
        hProperties: {
          className: ['version-zone__label'],
        },
      },
      children: [
        {
          type: 'strong',
          children: [{ type: 'text', value: 'Applies to:' }],
        },
        { type: 'text', value: ` ${versions}` },
      ],
    });
  }

  for (const child of node.children ?? []) {
    visit(child);
  }
}

export function remarkVersionZones() {
  return (tree) => visit(tree);
}
