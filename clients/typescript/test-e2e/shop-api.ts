import { type ChildProcess, spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { TestProject } from "vitest/node";

const port = 5091;
const repository = new URL("../../../", import.meta.url).pathname;

/** Starts samples/Shop.Api on SQLite with a throwaway database for the end-to-end tests. */
export default async function setup(project: TestProject): Promise<() => Promise<void>> {
  const directory = mkdtempSync(join(tmpdir(), "linqtorest-e2e-"));
  const api: ChildProcess = spawn(
    "dotnet",
    [
      "run",
      "--project",
      "samples/Shop.Api",
      "--",
      "--urls",
      `http://127.0.0.1:${String(port)}`,
      "--Database:Provider=Sqlite",
      `--ConnectionStrings:ShopSqlite=Data Source=${join(directory, "shop.db")}`,
    ],
    { cwd: repository, env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" }, stdio: "inherit" },
  );

  const baseUrl = `http://127.0.0.1:${String(port)}/api/`;
  for (let attempt = 0; ; attempt++) {
    try {
      const response = await fetch(`http://127.0.0.1:${String(port)}/openapi/v1.json`);
      if (response.ok) {
        break;
      }
    } catch {
      // Not listening yet.
    }

    if (attempt > 240 || api.exitCode !== null) {
      api.kill();
      throw new Error("samples/Shop.Api didn't start.");
    }

    await new Promise((resolve) => setTimeout(resolve, 500));
  }

  project.provide("baseUrl", baseUrl);
  return async () => {
    api.kill();
    await new Promise((resolve) => api.once("exit", resolve));
    rmSync(directory, { recursive: true, force: true });
  };
}

declare module "vitest" {
  export interface ProvidedContext {
    baseUrl: string;
  }
}
