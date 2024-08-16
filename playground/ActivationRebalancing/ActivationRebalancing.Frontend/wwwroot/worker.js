self.onmessage = function (e) {
  try {
    const data = e.data;
    const totalActivations = data.reduce((sum, d) => sum + d.activations, 0);
    const densityMatrix = Array.from({ length: 20 }, () => Array(20).fill('white'));

    data.forEach((d, i) => {
      const numCells = Math.round(d.activations / totalActivations * 400);
      const color = ['#ff0000', '#00ff00', '#0000ff', '#ffff00', '#ff00ff'][i % 5];

      let cellsFilled = 0;
      const maxFillWhiteCellAttempts = 500;

      for (let attempt = 0; attempt < maxFillWhiteCellAttempts && cellsFilled < numCells; attempt++) {
        const x = Math.floor(Math.random() * 20);
        const y = Math.floor(Math.random() * 20);
        if (densityMatrix[y][x] === 'white') {
          densityMatrix[y][x] = color;
          cellsFilled++;
        }
      }

    });

    postMessage({ densityMatrix });
  } catch (error) {
    postMessage({ error: error.message });
  }
};
