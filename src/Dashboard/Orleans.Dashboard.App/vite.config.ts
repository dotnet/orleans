import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import commonjs from "vite-plugin-commonjs";
import path from "path";

// https://vite.dev/config/
export default defineConfig({
  plugins: [commonjs(), react()],
  base: "./",
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  optimizeDeps: {
    include: ["react", "react-dom"],
  },
  build: {
    emptyOutDir: true,
    sourcemap: true,
    commonjsOptions: {
      include: [/node_modules/, /src/],
      transformMixedEsModules: true,
    },
    rollupOptions: {
      output: {
        manualChunks: undefined,
        inlineDynamicImports: true,
        entryFileNames: "index.min.js",
        assetFileNames: (assetInfo) =>
        {
          if (assetInfo.name?.match(/\.(woff|woff2|ttf|otf|eot)$/))
          {
            return "fonts/[name][extname]";
          }
          if (assetInfo.name?.match(/\.(svg|png|jpg|jpeg|gif|webp|ico)$/))
          {
            return "img/[name][extname]";
          }
          return "[name][extname]";
        },
      },
    },
  },
});
