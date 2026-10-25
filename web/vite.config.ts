import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The page talks to the map builder backend (MapBuilder.Server) on port 8770.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://127.0.0.1:8770' },
  },
});
