import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

/**
 * The site talks to two separate backends, one per game, because the two games have
 * nothing in common at the network layer: Destiny is a documented public API reachable
 * with a key, Halo is an undocumented one reachable only with a Spartan token minted from
 * an Xbox sign-in.
 *
 * In development each runs as its own `dotnet run`, so this proxy fronts them under a
 * single origin and the app can just call `/api/halo/...` and `/api/destiny/...`. That
 * matters for more than tidiness: without a shared origin every request is cross-origin,
 * and the browser would need CORS on both services purely to make local development work.
 *
 * Ports are overridable because two people will inevitably have something else on 5210.
 */
const halo = process.env.HALO_API ?? 'http://127.0.0.1:5210'
const destiny = process.env.DESTINY_API ?? 'http://127.0.0.1:5231'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api/halo': {
        target: halo,
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api\/halo/, '/api'),
      },
      '/api/destiny': {
        target: destiny,
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api\/destiny/, '/api'),
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
  },
})
