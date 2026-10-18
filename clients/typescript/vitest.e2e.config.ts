import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test-e2e/**/*.e2e.ts"],
    globalSetup: ["test-e2e/shop-api.ts"],
    testTimeout: 30_000,
    hookTimeout: 180_000,
    fileParallelism: false,
  },
});
