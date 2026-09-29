import { defineConfig } from 'vite';

export default defineConfig( {
	// relative asset paths: the build runs from any sub-path (GitHub Pages serves it under /tidewater/)
	base: './',
	build: { target: 'esnext', chunkSizeWarningLimit: 4000 },
	server: {
		port: 5188, strictPort: true, host: '127.0.0.1',
		// polling: the default watcher misses edits made from WSL on a Windows mount
		watch: { usePolling: true, interval: 300 },
	},
} );
