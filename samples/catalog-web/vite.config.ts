import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Ant Design makes one bundle of about 1 MB; a sample doesn't need code splitting to stay quiet about it.
export default defineConfig({ plugins: [react()], build: { chunkSizeWarningLimit: 1500 } });
