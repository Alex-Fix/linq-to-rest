import { type ChildProcess, spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { TestProject } from "vitest/node";

const port = 5091;
const repository = new URL("../../../", import.meta.url).pathname;

/** Starts samples/Catalog.Api (Development, SQLite) with a throwaway database for the end-to-end tests. */
export default async function setup(project: TestProject): Promise<() => Promise<void>> {
  const directory = mkdtempSync(join(tmpdir(), "linqtorest-e2e-"));
  const root = `http://127.0.0.1:${String(port)}/`;
  const api: ChildProcess = spawn(
    "dotnet",
    [
      "run",
      "--project",
      "samples/Catalog.Api",
      "--",
      "--urls",
      root,
      "--Database:Provider=Sqlite",
      `--ConnectionStrings:CatalogSqlite=Data Source=${join(directory, "catalog.db")}`,
    ],
    { cwd: repository, env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" }, stdio: "inherit" },
  );

  // Ready once the development sign-in answers (the database is seeded before the app listens).
  for (let attempt = 0; ; attempt++) {
    try {
      const response = await fetch(`${root}auth/dev-login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ role: "viewer" }),
      });
      if (response.ok) {
        break;
      }
    } catch {
      // Not listening yet.
    }

    if (attempt > 240 || api.exitCode !== null) {
      api.kill();
      throw new Error("samples/Catalog.Api didn't start.");
    }

    await new Promise((resolve) => setTimeout(resolve, 500));
  }

  project.provide("apiRoot", root);
  return async () => {
    api.kill();
    await new Promise((resolve) => api.once("exit", resolve));
    rmSync(directory, { recursive: true, force: true });
  };
}

declare module "vitest" {
  export interface ProvidedContext {
    apiRoot: string;
  }
}
