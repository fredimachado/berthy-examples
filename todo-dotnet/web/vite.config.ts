import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ command }) => {
  const apiHttp = process.env.API_HTTP;
  if (command === "serve" && (!apiHttp || apiHttp.trim() === "")) {
    throw new Error(
      "API_HTTP is required for the Vite development server. Run through Aspire.",
    );
  }

  return {
    plugins: [react()],
    server: {
      // Aspire assigns PORT; do not hardcode a host port.
      proxy: apiHttp
        ? {
            "/api": { target: apiHttp, changeOrigin: true },
            "/health": { target: apiHttp, changeOrigin: true },
            "/version": { target: apiHttp, changeOrigin: true },
          }
        : undefined,
    },
  };
});
